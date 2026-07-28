---
title: Reference
description: Configuration files, settings and contracts
---

# Reference

## Where configuration lives

| File | Holds |
|---|---|
| `appsettings.json` | Application-wide: global settings, health, web UI, logging |
| `environments/*.json` | One per environment: connections, tracked objects, destinations |

Environment files are watched and reloaded on save. `appsettings.json` is read at startup only.

## Precedence

Several settings exist in both places. The environment value wins where present:

```
environment value  →  ChangeTracking:GlobalSettings  →  built-in default
```

Overridable per environment: `PollingIntervalSeconds`, `ExportToFile`, `FilePath`, `ExportToApi`, `RetryCount`, `RetryDelaySeconds`.

Everything else is global.

## State

| File | Holds | If deleted |
|---|---|---|
| `state.db` | Last processed version per environment/object | Every object re-initialises per `InitialSyncMode` |
| `sinkhole.db` | Dead letters | Unreplayed failures are lost |
| `.core/` | Encryption keys and web UI data protection keys | Encrypted config becomes unreadable; UI sessions reset |

Back up `.core/` with the same care as the encryption key.

## Pages

- [Environment files](/reference/environment): connections, tracked objects, endpoints
- [Global settings](/reference/global-settings): every `appsettings.json` key
- [Authentication](/reference/authentication): endpoint auth types and web UI access
- [Stored procedure](/reference/stored-procedure): the JSON contract
- [Health endpoints](/reference/health): JSON status routes
