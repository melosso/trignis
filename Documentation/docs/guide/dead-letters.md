---
title: Dead letters
description: What happens when an export fails, and how to replay it
---

# Dead letters

When an export fails every retry, the payload is stored in `sinkhole.db` rather than dropped. Each destination fails independently. If the file write succeeds and the webhook does not, only the webhook produces a dead letter.

Dead letters are retried automatically on a widening backoff. What survives that is a genuine "someone has to look at this", and waits on the dashboard until you replay or discard it.

## Automatic replay

Every `DeadLetterReplayIntervalSeconds` (default 60) Trignis takes up to 25 dead letters whose backoff has elapsed and re-runs them. Each failure doubles the wait: 1, 2, 4, 8, then 16 minutes at the default base of 60 seconds, capped at six hours.

After `DeadLetterMaxReplayAttempts` (default 5) the row stops being retried and stays put. It is still listed, still readable, and still replayable by hand.

The batch limit and the backoff exist for the same reason: an endpoint down for an hour has a backlog, and dumping it back the instant it recovers is a thundering herd. Retrying 25 at a time on a widening interval drains the backlog without becoming the next outage.

A row whose environment or tracking object no longer exists is not retried at all. No amount of waiting brings back a deleted environment, so it surfaces immediately for a human instead of burning through attempts.

Turn it off with `DeadLetterAutoReplayEnabled: false`, or by setting `DeadLetterMaxReplayAttempts` to 0. Manual replay keeps working either way.

::: warning
Automatic replay is new in this release. Earlier versions never resent anything without being asked.
:::

## Working through them

The **Dead letters** page lists failures with their object, database, error and timestamp. Search covers object name, error text and database. Selecting a row opens the payload.

Three actions:

- **Replay**: re-runs the export through the same pipeline as a live change. The row is deleted only if every destination succeeds; a partial failure keeps it and reports which target refused. A manual replay also resets the attempt counter, on the assumption that you clicked it because you fixed something, so an exhausted row rejoins the automatic rotation.
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
      "DeadLetterMonitorEnabled": true,
      "DeadLetterAutoReplayEnabled": true,
      "DeadLetterReplayIntervalSeconds": 60,
      "DeadLetterMaxReplayAttempts": 5,
      "DeadLetterReplayBackoffSeconds": 60
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
