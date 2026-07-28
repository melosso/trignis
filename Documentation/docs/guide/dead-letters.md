---
title: Dead letters
description: What happens when an export fails, and how to replay it
---

# Dead letters

When an export fails every retry, the payload is stored in `sinkhole.db` rather than dropped. Each destination fails independently. If the file write succeeds and the webhook does not, only the webhook produces a dead letter.

Nothing is resent automatically. A dead letter waits until you replay or discard it.

## Why not auto-retry

An endpoint that has been down for an hour has a backlog. Replaying it automatically on recovery would produce a thundering herd, in an order nobody chose, possibly after the data stopped being relevant.

Replay is therefore explicit: fix the cause, then decide what to do with the backlog.

## Working through them

The **Dead letters** page lists failures with their object, database, error and timestamp. Search covers object name, error text and database. Selecting a row opens the payload.

Three actions:

- **Replay**: re-runs the export through the same pipeline as a live change. The row is deleted only if every destination succeeds; a partial failure keeps it and reports which target refused.
- **Discard**: deletes the row. The payload is gone.
- **Purge**: deletes everything matching the current filter. With no filter, that is everything.

::: warning
Replay sends to the environment's destinations **as configured now**, not as they were when the export failed. If the endpoint URL changed since, the replay goes to the new one.
:::

## Duplicates

Dead letters are unique per source and payload hash. The same payload failing repeatedly stores one row, not one per cycle. Different payloads from the same object store separately.

## Retention

Rows older than `DeadletterRetentionDays` (default 60) are purged at startup and every 24 hours after.

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "DeadletterRetentionDays": 60,
      "DeadLetterThreshold": 100,
      "DeadLetterCheckIntervalMinutes": 30,
      "DeadLetterMonitorEnabled": true
    }
  }
}
```

The monitor checks the queue every `DeadLetterCheckIntervalMinutes` and warns once the total passes `DeadLetterThreshold`, listing the worst-offending objects. Repeat warnings are rate-limited to one an hour.

::: tip
Retention is a deletion policy. If a dead letter matters, replay it before it ages out.
:::

## Rows from older versions

Dead letters written before environment tracking was added have no environment recorded and cannot be replayed, because the original target cannot be determined reliably. They can still be read and discarded. New rows are unaffected.
