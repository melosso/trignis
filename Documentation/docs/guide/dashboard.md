---
title: Dashboard
description: The built-in web UI
---

# Dashboard

Enable `WebHost` and browse to `http://localhost:2455`. Sign in with `Trignis:AdminApiKey`.

## Pages

| Page | Shows |
|---|---|
| **Dashboard** | Counts, service health, connection health, sync state per object |
| **Environments** | Loaded environments, their tracked objects and endpoints |
| **Settings** | Effective global settings and logging configuration, read-only |
| **Dead letters** | Failed exports, searchable, with [replay and discard](/guide/dead-letters) |
| **Logs** | Recent log output, filterable by level, with auto-refresh |

Credentials are never sent to the browser. Endpoint tokens, passwords, API keys and connection strings are stripped from the API responses that back these pages.

## Sync state

The dashboard lists every tracked object with its last processed version and when it last moved.

Each row has a **Reset** action, which clears the stored version. On the next cycle the object re-initialises according to its `InitialSyncMode`:

- `Incremental`: adopts the current version and carries on, exporting nothing for the gap.
- `Full`: re-exports every row.

Useful after fixing a stored procedure that returned bad data, or when a downstream system needs a complete refresh.

::: warning
Reset on a `Full` object re-exports the entire table. On a large table that is a substantial export. Check the mode before confirming.
:::

## Settings are read-only

The UI does not edit configuration. Environment files are the source of truth, and they are watched: save one and Trignis reloads that environment within a second, without a restart.

That keeps configuration reviewable in version control instead of drifting through a web form.
