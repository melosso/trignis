---
title: Track your first table
description: From an empty install to changes landing on disk
---

# Track your first table

End to end with one table, exporting to files. Swapping the destination later is a config change.

## 1. Write the stored procedure

Trignis asks your procedure for changes since a version, and it answers in JSON. Nothing is generated for you. You choose the columns.

For `dbo.Items` with `ItemCode`, `Description`, `Assortment` and `sysguid`:

```sql
CREATE OR ALTER PROCEDURE web.get_itemssync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion INT = JSON_VALUE(@json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;
        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Items'));

        IF (@fromVersion = 0)
            SET @reason = 0;                    -- first sync
        ELSE IF (@fromVersion < @minVer)
        BEGIN
            SET @fromVersion = 0;               -- history aged out; full sync needed
            SET @reason = 1;
        END

        IF (@fromVersion = 0)
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Full'  AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT ItemCode, Description, Assortment, sysguid
                    FROM dbo.Items
                    FOR JSON AUTO
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
        ELSE
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Diff'  AS 'Metadata.Sync.Type',
                [Data] = JSON_QUERY((
                    SELECT
                        ct.SYS_CHANGE_OPERATION AS '$operation',
                        ct.SYS_CHANGE_VERSION   AS '$version',
                        ct.ItemCode,
                        i.Description,
                        i.Assortment,
                        i.sysguid
                    FROM dbo.Items AS i
                    RIGHT OUTER JOIN CHANGETABLE(CHANGES dbo.Items, @fromVersion) AS ct
                        ON ct.ItemCode = i.ItemCode
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
    COMMIT TRAN;
END
```

Two details that matter:

- The **`RIGHT OUTER JOIN`** keeps deletes. The change table has the row, the base table no longer does.
- **`SNAPSHOT` isolation** keeps the version number and the rows consistent with each other.

Full contract, including column-level tracking, is in the [reference](/reference/stored-procedure).

## 2. Describe the environment

Create `environments/production.json`:

```json
{
  "ConnectionStrings": {
    "PrimaryDatabase": "Server=prod-sql.company.com;Database=PrimaryDB;Trusted_Connection=True;"
  },
  "ChangeTracking": {
    "ExportToFile": true,
    "FilePath": "exports/{environment}/{object}/changes-{timestamp}.json",
    "TrackingObjects": [
      {
        "Name": "Items",
        "Database": "PrimaryDatabase",
        "TableName": "dbo.Items",
        "StoredProcedureName": "web.get_itemssync"
      }
    ]
  }
}
```

The filename becomes the environment name. `Database` refers to a key in `ConnectionStrings`, not a server-side database name.

## 3. Start it

Trignis picks the file up without a restart, because it watches the folder. On startup it logs what it loaded:

```
├─ Environments: 1
│  └─ Environment: [production] (1 objects, 0 endpoints)
```

## 4. Watch the first cycle

The first run has no stored version, so behaviour depends on `InitialSyncMode`:

- **`Incremental`** (default): records the current version and exports nothing. Only future changes are sent.
- **`Full`**: exports every row first, then continues incrementally.

Default is deliberate: pointing Trignis at a large table should not immediately dump the whole thing downstream.

Change a row and wait for the next poll. A file appears under `exports/production/Items/`.

## Next

- [Files](/guide/export-files), [HTTP](/guide/export-http) or [queues](/guide/export-queues): other destinations.
- [Dashboard](/guide/dashboard): see sync state and reset it.
