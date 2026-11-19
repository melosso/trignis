---
title: Dashboard
description: The built-in web UI
---

# Dashboard

Trignis ships with a small web dashboard, which is usually the quickest way to see what it is actually doing without reading through log files. Setting `WebHost:Enabled` turns it on, and you can then browse to `http://localhost:2455` and sign in with your `Trignis:AdminApiKey`.

## Pages

| Page | Shows |
|---|---|
| **Dashboard** | Counts, service health, connection health, sync state per object |
| **Environments** | Loaded environments, their tracked objects and endpoints, and [pause controls](#pausing-change-tracking) |
| **Settings** | Effective global settings and logging configuration, read-only |
| **Dead letters** | Failed exports, searchable, with [replay and discard](/guide/dead-letters) |
| **Logs** | Recent log output, filterable by level, with auto-refresh |

One thing you can rely on while poking around: credentials never reach the browser. Endpoint tokens, passwords, API keys and connection strings are all stripped from the API responses behind these pages, so leaving the dashboard open on a second monitor does not put your secrets on screen.

## Sync state

The dashboard lists every tracked object alongside its last processed version and when that version last moved. Watching the timestamps is often enough to spot a problem: an object whose version has not advanced in a while is usually telling you something.

Each row also carries a **Reset** action, which clears the stored version. On the next cycle that object re-initialises according to its `InitialSyncMode`:

- `Incremental`: adopts the current version and carries on, exporting nothing for the gap.
- `Full`: re-exports every row.

This tends to come in handy after you have fixed a procedure that was returning something wrong, or when a downstream system has lost its copy and would like the whole thing again.

::: warning
Resetting a `Full` object re-exports its entire table, which on a large table is a substantial amount of data heading downstream. It is worth glancing at the sync mode before you confirm.
:::

## Pausing change tracking

Sometimes you need data to stop moving right now: a downstream system is mid-migration, or an endpoint is misbehaving and you would rather not fill its inbox. The **Environments** page lets you pause either a whole environment or a single tracking object for exactly these moments.

A paused scope is skipped on every cycle. No procedure is called, nothing is exported, and the stored version stops advancing where it is.

Pausing an environment holds everything inside it, including objects you add while the pause is in force. Pausing a single object is narrower and leaves its siblings running normally, which is usually what you want when only one integration is having a bad day.

### Why it is guarded

Pausing is the one action in the dashboard whose failure mode is silence. Nothing raises an error, no dead letter appears, and no health check turns red. Data simply stops moving, and it can be hours before anybody notices.

That is why the dialog spells out the consequences and asks for `Trignis:AdminApiKey` a second time, even though you are already signed in. It means a tab left open on a shared machine cannot pause production by itself.

Failed attempts here count towards the same lockout as the sign-in page, so ten wrong tries will lock the address out for 30 minutes.

Where no `AdminApiKey` is configured the dashboard has no sign-in at all, so there is nothing to step up from and the passphrase is not requested.

Resuming, by contrast, asks for nothing. Making somebody re-authenticate in order to restore service is a reliable way to make an incident longer than it needed to be.

::: warning
While a scope is paused, the source database carries on recording changes. Nothing is lost immediately, but the backlog does grow.

On SQL Server, a pause lasting longer than the table's `CHANGE_RETENTION` window ages that backlog out, and the next run falls back to a full sync. On PostgreSQL the outbox table keeps growing, and it cannot be trimmed past the stored version.

Pausing suits an outage or a migration window. For turning an integration off for good, removing it from the environment file is the cleaner option.
:::

### Where the pause lives

Pauses are recorded in `state.db`, next to the stored versions, rather than in your environment file.

That separation is deliberate. Your environment files describe what you intend to run and belong in version control, reviewed like any other change. A pause is something else: an operational decision made under time pressure, often by somebody who is not about to open a pull request.

Keeping them apart has two pleasant consequences. The dashboard never rewrites a file that you own, so nothing appears in your diff that you did not put there. And a config redeploy will not quietly un-pause an environment that somebody held for a good reason.

It also means a pause outlives a restart. It ends when someone resumes it, and not before.

## Settings are read-only

You may notice the Settings page shows values without letting you change them, and that the Environments page does not offer an editor. This is intentional rather than unfinished.

Environment files remain the source of truth, and they are watched: saving one reloads that environment within about a second, with no restart involved. Editing a file in your own editor is therefore already the fast path, and it leaves your configuration reviewable in version control instead of drifting through a web form nobody can diff.

Pause and resume are the deliberate exception, since they describe runtime state rather than configuration.
