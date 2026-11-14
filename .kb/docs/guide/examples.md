---
title: Examples
description: Two complete setups, one custom table and one from AdventureWorks
---

# Examples

Two full walkthroughs. The first builds a table from scratch, so you can run it on an empty database. The second tracks an existing AdventureWorks table, which is closer to what you will actually be doing.

Both follow the same four steps: create or pick a table, enable change tracking, write the procedure, point an environment at it.

## Example 1: a custom `dbo.Products` table

### The table

```sql
CREATE TABLE dbo.Products
(
    ProductId     INT             NOT NULL IDENTITY(1,1),
    Sku           NVARCHAR(50)    NOT NULL,
    Name          NVARCHAR(200)   NOT NULL,
    Category      NVARCHAR(100)   NULL,
    UnitPrice     DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_Products_UnitPrice DEFAULT (0),
    InStock       BIT             NOT NULL CONSTRAINT DF_Products_InStock   DEFAULT (1),
    ModifiedDate  DATETIME2(3)    NOT NULL CONSTRAINT DF_Products_Modified  DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT PK_Products     PRIMARY KEY CLUSTERED (ProductId),
    CONSTRAINT UQ_Products_Sku UNIQUE (Sku)
);
```

Change tracking needs a primary key. `ProductId` is it, and it is the value that appears in every change row, including deletes.

### Enable change tracking

```sql
ALTER DATABASE ShopDb
  SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

ALTER TABLE dbo.Products ENABLE CHANGE TRACKING;
```

`CHANGE_RETENTION` is how long history survives. If Trignis is stopped for longer than that, the stored version ages out and the next run falls back to a full sync.

### The procedure

```sql
CREATE OR ALTER PROCEDURE dbo.get_productssync
    @Json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @fromVersion INT = JSON_VALUE(@Json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT = 0;
        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Products'));

        IF (@fromVersion > 0 AND @fromVersion < @minVer)
        BEGIN
            SET @fromVersion = 0;   -- history aged out, start over
            SET @reason = 1;
        END

        IF (@fromVersion = 0)
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Full'  AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT
                        p.ProductId,
                        p.Sku,
                        p.Name,
                        p.Category,
                        p.UnitPrice,
                        p.InStock,
                        p.ModifiedDate
                    FROM dbo.Products AS p
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
        ELSE
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Diff'  AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT
                        ct.SYS_CHANGE_OPERATION AS '$operation',
                        ct.SYS_CHANGE_VERSION   AS '$version',
                        ct.ProductId,
                        p.Sku,
                        p.Name,
                        p.Category,
                        p.UnitPrice,
                        p.InStock,
                        p.ModifiedDate
                    FROM CHANGETABLE(CHANGES dbo.Products, @fromVersion) AS ct
                    LEFT OUTER JOIN dbo.Products AS p
                        ON p.ProductId = ct.ProductId
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
    COMMIT TRAN;
END
```

::: tip
Driving the join from `CHANGETABLE` with a `LEFT OUTER JOIN` keeps deleted rows, exactly like the `RIGHT OUTER JOIN` form in [Track your first table](/guide/first-table). Both work. Leading with the change table reads more naturally, because it is the side that always has a row.
:::

For a delete, only `$operation`, `$version` and `ProductId` carry values. Every other column is null, because the row is gone.

### The environment

`environments/shop.json`:

```json
{
  "ConnectionStrings": {
    "ShopDb": "Server=localhost;Database=ShopDb;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "ChangeTracking": {
    "PollingIntervalSeconds": 15,
    "ExportToFile": true,
    "FilePath": "exports/{environment}/{object}/changes-{timestamp}.json",
    "TrackingObjects": [
      {
        "Name": "Products",
        "Database": "ShopDb",
        "TableName": "dbo.Products",
        "StoredProcedureName": "dbo.get_productssync",
        "InitialSyncMode": "Full"
      }
    ]
  }
}
```

`InitialSyncMode: "Full"` is reasonable here: a product catalogue is small, and the downstream system probably wants the whole thing before it starts receiving updates.

### Try it

```sql
INSERT INTO dbo.Products (Sku, Name, Category, UnitPrice)
VALUES ('SKU-001', 'Blue Widget', 'Widgets', 9.99),
       ('SKU-002', 'Red Widget',  'Widgets', 12.50);

UPDATE dbo.Products SET UnitPrice = 11.00 WHERE Sku = 'SKU-001';
DELETE FROM dbo.Products WHERE Sku = 'SKU-002';
```

Within one polling interval a file appears under `exports/shop/Products/`:

```json
[
  { "$operation": "U", "$version": 3, "ProductId": 1, "Sku": "SKU-001", "Name": "Blue Widget", "Category": "Widgets", "UnitPrice": 11.00, "InStock": true, "ModifiedDate": "2026-07-28T10:15:00.000" },
  { "$operation": "D", "$version": 4, "ProductId": 2 }
]
```

Two changes, not three. The insert and the update to `SKU-001` collapse into one row at the latest version, which is the point of polling. `SKU-002` was inserted and deleted between polls, so only the delete survives.

## Example 2: `Sales.SalesOrderHeader` from AdventureWorks

A real table, wider, with computed columns and foreign keys. Restore the AdventureWorks sample database first.

### Enable change tracking

```sql
ALTER DATABASE AdventureWorks2026
  SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 3 DAYS, AUTO_CLEANUP = ON);

ALTER TABLE Sales.SalesOrderHeader ENABLE CHANGE TRACKING;
```

### The procedure

Do not export all 26 columns out of habit. Pick what the downstream system needs.

```sql
CREATE OR ALTER PROCEDURE Sales.get_salesorderssync
    @Json NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @fromVersion INT = JSON_VALUE(@Json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT = 0;
        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('Sales.SalesOrderHeader'));

        IF (@fromVersion > 0 AND @fromVersion < @minVer)
        BEGIN
            SET @fromVersion = 0;
            SET @reason = 1;
        END

        IF (@fromVersion = 0)
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Full'  AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT
                        soh.SalesOrderID,
                        soh.SalesOrderNumber,
                        soh.OrderDate,
                        soh.DueDate,
                        soh.ShipDate,
                        soh.Status,
                        soh.CustomerID,
                        soh.SubTotal,
                        soh.TaxAmt,
                        soh.Freight,
                        soh.TotalDue,
                        soh.ModifiedDate
                    FROM Sales.SalesOrderHeader AS soh
                    WHERE soh.OrderDate >= DATEADD(YEAR, -1, SYSUTCDATETIME())
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
        ELSE
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Diff'  AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT
                        ct.SYS_CHANGE_OPERATION AS '$operation',
                        ct.SYS_CHANGE_VERSION   AS '$version',
                        ct.SalesOrderID,
                        soh.SalesOrderNumber,
                        soh.OrderDate,
                        soh.DueDate,
                        soh.ShipDate,
                        soh.Status,
                        soh.CustomerID,
                        soh.SubTotal,
                        soh.TaxAmt,
                        soh.Freight,
                        soh.TotalDue,
                        soh.ModifiedDate
                    FROM CHANGETABLE(CHANGES Sales.SalesOrderHeader, @fromVersion) AS ct
                    LEFT OUTER JOIN Sales.SalesOrderHeader AS soh
                        ON soh.SalesOrderID = ct.SalesOrderID
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
    COMMIT TRAN;
END
```

Three things this shows:

- **Computed columns are fine.** `SalesOrderNumber` and `TotalDue` are computed, and select like any other column.
- **The full sync is filtered, the diff is not.** `WHERE soh.OrderDate >= ...` keeps the initial load to a year of orders instead of every order ever placed. The diff branch has no filter, because a change to an older order is still a change you want.
- **`Status` is a `TINYINT`.** It arrives as a number, not a label. Map it downstream, or add a `CASE` here if the receiver wants text.

::: warning
That filtered full sync has a consequence: an order older than a year is invisible until somebody touches it, and then it arrives as an update for a record the downstream system has never seen. Make sure the receiver can handle an update for an unknown key, or drop the `WHERE` clause.
:::

### The environment

`environments/adventureworks.json`:

```json
{
  "ConnectionStrings": {
    "AdventureWorks": "Server=sql.example.com;Database=AdventureWorks2026;User Id=trignis;Password=changeme;TrustServerCertificate=True;"
  },
  "ChangeTracking": {
    "PollingIntervalSeconds": 60,
    "ExportToApi": true,
    "RetryCount": 5,
    "RetryDelaySeconds": 10,
    "TrackingObjects": [
      {
        "Name": "SalesOrders",
        "Database": "AdventureWorks",
        "TableName": "Sales.SalesOrderHeader",
        "StoredProcedureName": "Sales.get_salesorderssync",
        "InitialSyncMode": "Full"
      }
    ],
    "ApiEndpoints": [
      {
        "Key": "erp_orders",
        "Url": "https://erp.example.com/api/orders/changes",
        "Auth": {
          "Type": "Bearer",
          "Token": "replace-me"
        },
        "CustomHeaders": {
          "X-Correlation-Id": "{guid}",
          "X-Source": "trignis-{environment}"
        },
        "EnableCompression": true
      }
    ]
  }
}
```

The password is plaintext only until Trignis first reads the file. It is encrypted in place on startup and the file is rewritten, so what stays on disk is a `PWENC:` value.

### Watch the first sync

A year of AdventureWorks orders is a few thousand rows, which is more than one comfortable HTTP request. Batching splits it:

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "EnablePayloadBatching": true,
      "MaxRecordsPerBatch": 500
    }
  }
}
```

The receiver gets several POSTs, each carrying `X-Batch-Number` and `X-Total-Batches`. Buffer them and commit when the last one lands. See [batching](/guide/export-http#batching) for why that matters.

### Tracking the detail lines too

Orders usually need their line items. `Sales.SalesOrderDetail` is a second tracked object, not extra columns on the first:

```sql
ALTER TABLE Sales.SalesOrderDetail ENABLE CHANGE TRACKING;
```

```json
{
  "Name": "SalesOrderLines",
  "Database": "AdventureWorks",
  "TableName": "Sales.SalesOrderDetail",
  "StoredProcedureName": "Sales.get_salesorderlinessync",
  "InitialSyncMode": "Full"
}
```

::: warning
Each object has its own version and polls independently, so a header and its lines can arrive in either order and in different batches. There is no transaction across objects. The receiver has to tolerate lines for an order it has not seen yet, usually by staging them until the header appears.

If that is unacceptable, return the lines nested inside the header payload with a `JSON_QUERY` subselect and track only the header. The cost is that a line-only edit does not bump the header's change version, so it will not be picked up.
:::

## Where to go next

- [Stored procedure contract](/reference/stored-procedure) for the full output rules and column-level tracking.
- [Environment files](/reference/environment) for every field used above.
- [Dead letters](/guide/dead-letters) for what happens when the receiving end refuses.
