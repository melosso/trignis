---
title: Stored procedure contract
description: The JSON shape Trignis expects back
---

# Stored procedure contract

Trignis reads changes only through a procedure you write. Nothing is generated against your tables, so the columns that leave the database are your decision.

The contract below is the same on every [provider](/reference/environment#provider). What differs is how the procedure is invoked and where the watermark comes from; see [SQL Server](#sql-server) and [PostgreSQL](#postgresql).

## Input

One JSON parameter:

```json
{ "fromVersion": 12345, "mode": "sync" }
```

| Field | Meaning |
|---|---|
| `fromVersion` | Last version Trignis processed, or `0` on a first run or after a reset |
| `mode` | `sync` normally, `seed` when Trignis is establishing a starting point |

`mode` is `seed` only on the very first cycle of an object set to `InitialSyncMode: Incremental`, on providers where Trignis cannot read a watermark from the server. It means **report your current version and return no rows**. Trignis discards any rows returned during a seed and logs a warning, so a procedure that ignores `mode` degrades to "skips the first batch" rather than flooding every destination with history.

On SQL Server `mode` is always `sync`, because the server reports the watermark itself. Procedures written before `mode` existed keep working untouched.

## Output

One JSON document:

```json
{
  "Metadata": {
    "Sync": {
      "Version": 12345,
      "Type": "Full",
      "ReasonCode": 0
    }
  },
  "Data": [
    {
      "$operation": "I",
      "$version": 12345,
      "ItemCode": "A-100",
      "Description": "Widget"
    }
  ]
}
```

### Metadata.Sync

| Field | Type | Meaning |
|---|---|---|
| `Version` | integer | Version reached by this call |
| `Type` | string | `Full` or `Diff`, informational |
| `ReasonCode` | int | `0` first sync, `1` `fromVersion` too old |

`Metadata.Sync.Version` is required. Trignis fails the object if it is missing.

### Data

An array of changes, or absent when there are none. Each element may carry:

- `$operation`: `I`, `U` or `D`
- `$version`: that row's version

Everything else is yours.

## What a version has to be

A version is **any integer that only ever increases, and that a row is assigned no later than the moment it becomes visible to a reader.**

SQL Server's `SYS_CHANGE_VERSION` satisfies this and is the obvious choice there, but nothing in Trignis requires change tracking. A sequence, an outbox table's identity column, or an existing `rowversion` all work, on any supported provider. Whatever you pick has to hold two properties:

**Monotonic.** A version that can go down means Trignis skips whatever it passed over.

**Assigned no earlier than commit, or gated to exclude in-flight work.** This is the one that bites. Sequences hand out numbers at insert time, not commit time. Two transactions take 100 and 101; 101 commits first. A poll that sees 101 and stores it never sees 100. The SQL Server section handles this with snapshot isolation, the PostgreSQL section with a transaction horizon. Both do the same job.

Timestamps satisfy neither reliably. Do not use them.

Versions are read as 64-bit signed integers.

## How the version is chosen

After a successful export Trignis stores the highest `$version` it saw. If no row carries one, it stores `Metadata.Sync.Version`.

That matters: reporting a version higher than the changes you actually returned skips the gap permanently.

The version only advances after every destination has accepted the batch. A destination failing every cycle means the version never moves and the same rows are retried. Check the [dead letters](/guide/dead-letters).

---

## SQL Server

**Provider:** `mssql` (the default)

**Invoked as:** `SET NOCOUNT ON; EXEC <your procedure> @Json = @JsonParam;`

Write a stored procedure taking one `NVARCHAR(MAX)` parameter named `@Json`.

```sql
DECLARE @fromVersion BIGINT = JSON_VALUE(@json, '$.fromVersion');
```

Command timeout is 300 seconds.

### Watermark

Trignis reads `CHANGE_TRACKING_CURRENT_VERSION()` itself to seed an incremental first sync, so `mode` is always `sync` here and the procedure never has to handle seeding.

### Requirements

**Return a single JSON document.** Use `FOR JSON PATH, WITHOUT_ARRAY_WRAPPER`. SQL Server may split large results across rows; Trignis reassembles them, so chunking is fine, but the concatenation must parse as one document.

**Use snapshot isolation.** Reading the version and the rows in one snapshot keeps them consistent, and is what stops the in-flight transaction gap described above.

```sql
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
```

**Handle an aged-out version.** Compare against `CHANGE_TRACKING_MIN_VALID_VERSION`. Older than that and history is gone, so fall back to a full sync:

```sql
IF (@fromVersion < @minVer)
BEGIN
    SET @fromVersion = 0;
    SET @reason = 1;
END
```

**Keep deletes.** `RIGHT OUTER JOIN` from the base table to `CHANGETABLE`. An inner join silently drops every delete, because the row is gone from the base table.

**Do not use `INCLUDE_NULL_VALUES` with column tracking.** Unchanged columns come back null, and including them makes an untouched column indistinguishable from one set to null.

**Emit which columns changed.** `FOR JSON PATH` omits null properties, so on its own a column cleared to `NULL` and a column left untouched both arrive as a missing key. A consumer that reasonably treats a missing key as "unchanged" then drops the clearing, and the version moves past it for good. Emitting the mask as a `$changed` array removes the ambiguity; see [column-level tracking](#column-level-tracking) below.

### Column-level tracking

See [Track your first table](/guide/first-table) for a complete procedure, and `Source/SQL/02-stored-procedure.sqlserver.column.sql` for a working column-level one. The mechanism is `CHANGE_TRACKING_IS_COLUMN_IN_MASK` paired with `COLUMNPROPERTY`, which lets you emit only the columns that actually changed:

```sql
DECLARE @Description INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Description', 'ColumnId');

SELECT
    ct.SYS_CHANGE_OPERATION AS '$operation',
    ct.SYS_CHANGE_VERSION   AS '$version',
    ct.ItemCode,
    CASE WHEN ct.SYS_CHANGE_OPERATION = 'I'
           OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Description, ct.SYS_CHANGE_COLUMNS) = 1
         THEN i.Description END AS Description
FROM dbo.Items AS i
RIGHT OUTER JOIN CHANGETABLE(CHANGES dbo.Items, @fromVersion) AS ct
    ON ct.ItemCode = i.ItemCode
FOR JSON PATH
```

Column tracking has to be enabled on the table before any of this reports anything:

```sql
ALTER TABLE dbo.Items ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
```

The same mask also builds the `$changed` array described above:

```sql
CASE WHEN ct.SYS_CHANGE_OPERATION = 'U' THEN JSON_QUERY((
    SELECT '["' + STRING_AGG(c.name, '","') + '"]'
    FROM (VALUES ('Description', @Description), ('Assortment', @Assortment)) AS c(name, colid)
    WHERE CHANGE_TRACKING_IS_COLUMN_IN_MASK(c.colid, ct.SYS_CHANGE_COLUMNS) = 1
)) END AS '$changed'
```

One prerequisite that is easy to miss: snapshot isolation has to be allowed at the database level, or the procedure fails with "snapshot isolation is not allowed in this database".

```sql
ALTER DATABASE YourDatabase SET ALLOW_SNAPSHOT_ISOLATION ON;
```

---

## PostgreSQL

**Provider:** `postgres`

**Invoked as:** `SELECT <your function>(@JsonParam::json)`

Write a **`FUNCTION`, not a `PROCEDURE`**. A PostgreSQL procedure cannot return a value, and Trignis needs one. Return `json`, `jsonb` or `text`.

PostgreSQL has no built-in equivalent of SQL Server change tracking, so you supply the watermark. Trignis sends `mode: "seed"` on the first incremental cycle to ask for it.

### Capture deletes with an outbox table

A version column on the base table cannot report deletes: the row is gone, so nothing carries the version. This is the same trap as the SQL Server `RIGHT OUTER JOIN` note, and on PostgreSQL the fix is a small outbox table fed by a trigger.

```sql
CREATE TABLE items_outbox (
    id          BIGSERIAL PRIMARY KEY,
    xact_id     xid8    NOT NULL DEFAULT pg_current_xact_id(),
    operation   CHAR(1) NOT NULL,
    item_code   TEXT    NOT NULL,
    description TEXT
);

CREATE INDEX items_outbox_id_idx ON items_outbox (id);

CREATE FUNCTION items_outbox_capture() RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        INSERT INTO items_outbox (operation, item_code, description)
        VALUES ('D', OLD.item_code, NULL);
        RETURN OLD;
    END IF;

    INSERT INTO items_outbox (operation, item_code, description)
    VALUES (CASE TG_OP WHEN 'INSERT' THEN 'I' ELSE 'U' END, NEW.item_code, NEW.description);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER items_capture
AFTER INSERT OR UPDATE OR DELETE ON items
FOR EACH ROW EXECUTE FUNCTION items_outbox_capture();
```

`pg_current_xact_id()` requires PostgreSQL 13 or newer. On 12 and earlier use `txid_current()` with a `BIGINT` column and `txid_snapshot_xmin(txid_current_snapshot())` below.

### The transaction horizon

`BIGSERIAL` assigns an id when the row is inserted, not when the transaction commits, so ids become visible out of order. Reading `MAX(id)` and storing it will silently skip any lower id still in flight.

The fix is to ignore everything newer than the oldest transaction still running:

```sql
pg_snapshot_xmin(pg_current_snapshot())
```

Every row whose `xact_id` is below that is committed and visible for good. Rows at or above it are excluded this cycle and picked up by the next one, in order. This is what snapshot isolation does for you on SQL Server; here it is explicit.

Filter **and** compute the watermark through the same horizon. Using the horizon for the rows but `MAX(id)` for the version reintroduces exactly the gap it exists to prevent.

### The function

```sql
CREATE OR REPLACE FUNCTION get_item_changes(payload json)
RETURNS json AS $$
DECLARE
    from_version BIGINT  := COALESCE((payload ->> 'fromVersion')::BIGINT, 0);
    seeding      BOOLEAN := COALESCE(payload ->> 'mode', 'sync') = 'seed';
    horizon      xid8    := pg_snapshot_xmin(pg_current_snapshot());
    max_version  BIGINT;
    rows_json    json;
BEGIN
    SELECT COALESCE(MAX(id), from_version)
      INTO max_version
      FROM items_outbox
     WHERE xact_id < horizon;

    IF seeding THEN
        RETURN json_build_object(
            'Metadata', json_build_object('Sync', json_build_object(
                'Version', max_version, 'Type', 'Diff', 'ReasonCode', 0)),
            'Data', '[]'::json);
    END IF;

    SELECT COALESCE(json_agg(json_build_object(
               '$operation',  o.operation,
               '$version',    o.id,
               'ItemCode',    o.item_code,
               'Description', o.description
           ) ORDER BY o.id), '[]'::json)
      INTO rows_json
      FROM items_outbox o
     WHERE o.id > from_version
       AND o.xact_id < horizon;

    RETURN json_build_object(
        'Metadata', json_build_object('Sync', json_build_object(
            'Version', max_version, 'Type', 'Diff', 'ReasonCode', 0)),
        'Data', rows_json);
END;
$$ LANGUAGE plpgsql;
```

Point an environment at it:

```json
{
  "Provider": "postgres",
  "ConnectionStrings": {
    "PrimaryDatabase": "Host=pg.example.com;Database=primary;Username=trignis;Password=..."
  },
  "ChangeTracking": {
    "TrackingObjects": [
      {
        "Name": "Items",
        "Database": "PrimaryDatabase",
        "TableName": "public.items",
        "StoredProcedureName": "get_item_changes"
      }
    ]
  }
}
```

### Column-level tracking

If your consumers only want the columns that actually moved, `Source/SQL/02-stored-procedure.postgres.column.sql` is a working variant. It adds a `changed TEXT[]` column to the outbox, has the trigger fill it by comparing `OLD` against `NEW` with `IS DISTINCT FROM`, and emits each column only when the change touched it.

One detail is worth borrowing even if you write your own. Stripping nulls from the payload is tempting, but on its own it makes a column that was set to `NULL` indistinguishable from one that was never touched, which is the same trap as `INCLUDE_NULL_VALUES` on SQL Server. The example therefore also emits the list of changed columns:

```json
{ "$operation": "U", "$version": 8, "Id": 1, "$changed": ["AdjustedSteps"] }
```

`$changed` carries the names as they appear in the payload rather than the underlying column names, so no mapping is needed on the way out.

Reading that downstream: a name in `$changed` with a matching key carries the new value, a name in `$changed` with no key means the column was cleared to `NULL`, and anything in neither place did not change and is best left alone. The SQL Server example emits the same `$changed` array, so a consumer can be written once and used against either provider.

### Cleaning up

The outbox grows forever unless you trim it. Trignis does not do it for you, and it cannot: it does not know which consumer is furthest behind. Delete below the watermark Trignis reports on the dashboard, keeping a margin:

```sql
DELETE FROM items_outbox WHERE id <= <last processed version> - 100000;
```

Trimming above the stored watermark loses changes permanently.

### Row-level security

Functions run as the caller by default. If the tracked table has RLS policies, the Trignis role sees only what those policies allow, and changes it cannot see are skipped silently. Either grant the role `BYPASSRLS` or declare the function `SECURITY DEFINER` owned by a role that can read everything.

::: warning
A `SECURITY DEFINER` function with an unpinned `search_path` is a privilege escalation route: the caller can put a schema of their own in front and have the function run their objects as the owner. Pin it:

```sql
ALTER FUNCTION get_item_changes(json) SET search_path = public, pg_temp;
```
:::

---

## Troubleshooting

**Malformed JSON.** Parse failures are logged with the payload length and its first and last 200 characters. At `Debug` level the full response is written to `debug/` for inspection.

**Nothing exported.** With `InitialSyncMode: Incremental`, the first run records the current version and exports nothing. That is intended. Use `Full`, or change a row and wait for the next cycle.

**"returned rows during an incremental seed; discarding them".** The procedure ignored `mode: "seed"`. Return no rows in that case, or set `InitialSyncMode` to `Full` if you did want the history.

**Same rows repeatedly.** The version is only advanced after a successful export. A destination failing every cycle means the version never moves. Check the [dead letters](/guide/dead-letters).

**Changes appear late, or in bursts, on PostgreSQL.** Expected when long-running transactions hold the horizon back. `SELECT pg_snapshot_xmin(pg_current_snapshot());` against `SELECT MAX(xact_id) FROM items_outbox;` shows the lag.
