---
title: Track your first table
description: From an empty install to changes landing on disk
---

# Track your first table

End to end with one table, exporting to files. Swapping the destination later is a config change.

Each code block has a SQL Server and a PostgreSQL tab. SQL Server uses change tracking. PostgreSQL uses an outbox table filled by a trigger.

## 1. Write the stored procedure

Trignis asks your procedure for changes since a version, and it answers in JSON. Nothing is generated for you. You choose the columns.

For an items table with `ItemCode`, `Description`, `Assortment` and `sysguid`:

::: code-group
```sql [SQL Server icon:database]
CREATE OR ALTER PROCEDURE web.get_itemssync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion BIGINT = JSON_VALUE(@json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;
        DECLARE @curVer BIGINT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer BIGINT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Items'));

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
```sql [PostgreSQL icon:postgresql]
-- One outbox row per change, deletes included.
CREATE TABLE public.items_outbox
(
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    xact_id     xid8    NOT NULL DEFAULT pg_current_xact_id(),
    operation   CHAR(1) NOT NULL,
    item_code   TEXT    NOT NULL
);

CREATE OR REPLACE FUNCTION public.items_capture() RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO public.items_outbox (operation, item_code)
    VALUES (LEFT(TG_OP, 1), CASE WHEN TG_OP = 'DELETE' THEN OLD.item_code ELSE NEW.item_code END);
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER items_capture AFTER INSERT OR UPDATE OR DELETE ON public.items
FOR EACH ROW EXECUTE FUNCTION public.items_capture();

CREATE SCHEMA IF NOT EXISTS web;

CREATE OR REPLACE FUNCTION web.get_itemssync(payload json) RETURNS json AS $$
DECLARE
    from_version BIGINT := COALESCE((payload ->> 'fromVersion')::BIGINT, 0);
    horizon      xid8   := pg_snapshot_xmin(pg_current_snapshot());
    -- Every transaction below the horizon has finished.
    max_version  BIGINT := horizon::text::BIGINT - 1;
BEGIN

    IF payload ->> 'mode' = 'seed' THEN
        RETURN json_build_object('Metadata', json_build_object('Sync', json_build_object('Version', max_version, 'Type', 'Diff')));
    END IF;

    IF from_version = 0 THEN
        RETURN json_build_object(
            'Metadata', json_build_object('Sync', json_build_object('Version', max_version, 'Type', 'Full')),
            'Data', (SELECT COALESCE(json_agg(json_build_object(
                        'ItemCode', i.item_code, 'Description', i.description, 'Assortment', i.assortment, 'sysguid', i.sysguid)), '[]'::json)
                       FROM public.items i));
    END IF;

    RETURN json_build_object(
        'Metadata', json_build_object('Sync', json_build_object('Version', max_version, 'Type', 'Diff')),
        'Data', (SELECT COALESCE(json_agg(json_build_object(
                    '$operation', c.operation, '$version', c.xact_id::text::BIGINT,
                    'ItemCode', c.item_code, 'Description', i.description, 'Assortment', i.assortment, 'sysguid', i.sysguid)), '[]'::json)
                   FROM (SELECT DISTINCT ON (item_code) * FROM public.items_outbox
                          WHERE xact_id::text::BIGINT > from_version AND xact_id < horizon
                          ORDER BY item_code, xact_id DESC, id DESC) c
                   LEFT JOIN public.items i ON i.item_code = c.item_code));
END;
$$ LANGUAGE plpgsql;
```
:::

Two details that matter:

- **Deletes:** on SQL Server the `RIGHT OUTER JOIN` keeps them, because the change table has the row and the base table does not. On PostgreSQL the trigger writes them to the outbox.
- **Consistent versions:** on SQL Server `SNAPSHOT` isolation keeps the version number and the rows consistent. On PostgreSQL the `pg_snapshot_xmin` horizon defers changes from transactions that are still open.

Full contract, including column-level tracking, is in the [reference](/reference/stored-procedure).

## 2. Describe the environment

Create `environments/production.json`:

::: code-group
```json [SQL Server icon:database]
{
  "Provider": "mssql",
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
```json [PostgreSQL icon:postgresql]
{
  "Provider": "postgres",
  "ConnectionStrings": {
    "PrimaryDatabase": "Host=prod-pg.company.com;Database=primarydb;Username=trignis;Password=..."
  },
  "ChangeTracking": {
    "ExportToFile": true,
    "FilePath": "exports/{environment}/{object}/changes-{timestamp}.json",
    "TrackingObjects": [
      {
        "Name": "Items",
        "Database": "PrimaryDatabase",
        "TableName": "public.items",
        "StoredProcedureName": "web.get_itemssync"
      }
    ]
  }
}
```
:::

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
