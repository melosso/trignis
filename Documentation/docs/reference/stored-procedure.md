---
title: Stored procedure contract
description: The JSON shape Trignis expects back
---

# Stored procedure contract

Trignis reads changes only through a procedure you write. Nothing is generated against your tables, so the columns that leave the database are your decision.

## Input

One parameter, `@Json`, an `NVARCHAR(MAX)` document:

```json
{ "fromVersion": 12345 }
```

`fromVersion` is the last version Trignis processed, or `0` on a first run or after a reset.

```sql
DECLARE @fromVersion INT = JSON_VALUE(@json, '$.fromVersion');
```

Command timeout is 300 seconds.

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
| `Version` | int | Version reached, normally `CHANGE_TRACKING_CURRENT_VERSION()` |
| `Type` | string | `Full` or `Diff`, informational |
| `ReasonCode` | int | `0` first sync, `1` `fromVersion` too old |

`Metadata.Sync.Version` is required. Trignis fails the object if it is missing.

### Data

An array of changes, or absent when there are none. Each element may carry:

- `$operation`: `I`, `U` or `D` from `SYS_CHANGE_OPERATION`
- `$version`: that row's `SYS_CHANGE_VERSION`

Everything else is yours.

## How the version is chosen

After a successful export Trignis stores the highest `$version` it saw. If no row carries one, it stores `Metadata.Sync.Version`.

That matters: reporting a version higher than the changes you actually returned skips the gap permanently.

## Requirements

**Return a single JSON document.** Use `FOR JSON PATH, WITHOUT_ARRAY_WRAPPER`. SQL Server may split large results across rows; Trignis reassembles them, so chunking is fine, but the concatenation must parse as one document.

**Use snapshot isolation.** Reading the version and the rows in one snapshot keeps them consistent.

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

## Full example

See [Track your first table](/guide/first-table) for a complete table-tracking procedure.

For column-level tracking, use `CHANGE_TRACKING_IS_COLUMN_IN_MASK` with `COLUMNPROPERTY` to emit only the columns that actually changed:

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

Column tracking must be enabled on the table:

```sql
ALTER TABLE dbo.Items ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
```

## Troubleshooting

**Malformed JSON.** Parse failures are logged with the payload length and its first and last 200 characters. At `Debug` level the full response is written to `debug/` for inspection.

**Nothing exported.** With `InitialSyncMode: Incremental`, the first run records the current version and exports nothing. That is intended. Use `Full`, or change a row and wait for the next cycle.

**Same rows repeatedly.** The version is only advanced after a successful export. A destination failing every cycle means the version never moves. Check the [dead letters](/guide/dead-letters).
