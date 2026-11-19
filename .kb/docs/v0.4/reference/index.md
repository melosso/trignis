---
title: Reference
description: Configuration files, settings and contracts
---

# Reference

This section is the part you come back to rather than read through once. If you are still getting your bearings, the [guide](/guide/) is a friendlier starting point and will send you here when you need the detail.

## Where configuration lives

Trignis splits its configuration across two kinds of file, and knowing which is which saves a lot of guessing:

| File | Holds |
|---|---|
| `appsettings.json` | Application-wide: global settings, health, web UI, logging |
| `environments/*.json` | One per environment: connections, tracked objects, destinations |

The split is not arbitrary. Environment files are watched, so saving one reloads that environment within about a second and nothing needs restarting. `appsettings.json` is read once at startup, because the things it controls (which ports to bind, how logging is wired) cannot meaningfully change underneath a running process.

In practice that means you can iterate freely on what you track and where it goes, and you only reach for a restart when you change how the service itself runs.

## Precedence

A handful of settings appear in both places, which lets you set a sensible default once and let a single environment differ. When Trignis needs a value it walks this chain and takes the first thing it finds:

```
environment value  →  ChangeTracking:GlobalSettings  →  built-in default
```

Six settings can be overridden per environment: `PollingIntervalSeconds`, `ExportToFile`, `FilePath`, `ExportToApi`, `RetryCount` and `RetryDelaySeconds`. Everything else stays global, which keeps the surface small enough to reason about.

A common shape is a slow polling interval globally with one busy environment turned up, rather than repeating the same value in every file.

## State

Alongside your configuration, Trignis keeps a few files of its own. These are not meant to be edited by hand, but it helps to know what each one is protecting:

| File | Holds | If deleted |
|---|---|---|
| `state.db` | Last processed version per object, and any [pauses](/guide/dashboard#pausing-change-tracking) | Every object re-initialises per `InitialSyncMode` |
| `sinkhole.db` | Dead letters | Unreplayed failures are lost |
| `.core/` | Encryption keys and web UI data protection keys | Encrypted config becomes unreadable, and UI sessions reset |

`.core/` deserves the same care as the encryption key itself, so please include it in whatever you back up. Losing it means re-entering every secret by hand.

## Pages

- [Environment files](/reference/environment): connections, tracked objects, endpoints
- [Global settings](/reference/global-settings): every `appsettings.json` key
- [Authentication](/reference/authentication): endpoint auth types and dashboard access
- [Procedure contract](/reference/stored-procedure): the JSON shape, for both SQL Server and PostgreSQL
- [Health endpoints](/reference/health): JSON status routes
