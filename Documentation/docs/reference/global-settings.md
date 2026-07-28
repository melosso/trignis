---
title: Global settings
description: Every appsettings.json key
---

# Global settings

`appsettings.json` is read once at startup. Changes need a restart.

```json
{
  "Trignis": { "AdminApiKey": "change-me" },
  "Health":  { "Enabled": true, "Port": 2455, "Host": "*", "CacheDurationSeconds": 120 },
  "WebHost": { "Enabled": true, "Host": "*" },
  "Windows": { "UseEventLog": false },
  "ChangeTracking": {
    "StateDbPath": "state.db",
    "GlobalSettings": {
      "PollingIntervalSeconds": 30,
      "ExportToFile": false,
      "FilePath": "exports/{environment}/{object}/{database}/changes-{timestamp}.json",
      "FilePathSizeLimit": 500,
      "ExportToApi": false,
      "RetryCount": 3,
      "RetryDelaySeconds": 5,
      "DeadletterRetentionDays": 30,
      "DeadLetterThreshold": 100,
      "DeadLetterCheckIntervalMinutes": 30,
      "DeadLetterMonitorEnabled": true,
      "HealthCheckEnabled": true,
      "HealthCheckIntervalMinutes": 15,
      "MaxPayloadSizeBytes": 5242880,
      "MaxRecordsPerBatch": 1000,
      "EnablePayloadBatching": true
    }
  }
}
```

## ChangeTracking:GlobalSettings

Defaults for every environment. Six can be overridden per environment; the rest are global.

### Polling and export

| Key | Default | Purpose | Per-env |
|---|---|---|---|
| `PollingIntervalSeconds` | 30 | Seconds between cycles | yes |
| `ExportToFile` | false | Write to disk | yes |
| `FilePath` | `exports/{environment}/...` | Output template | yes |
| `FilePathSizeLimit` | 500 | Export directory cap in MB | no |
| `ExportToApi` | false | Send to endpoints | yes |

Below 5 seconds or above 3600 logs a warning; neither is rejected.

### Retries

| Key | Default | Purpose | Per-env |
|---|---|---|---|
| `RetryCount` | 3 | Attempts before a dead letter | yes |
| `RetryDelaySeconds` | 5 | Fixed delay between attempts | yes |

The delay is fixed, not exponential. Retries cover transport errors, IO errors and SQL errors. A payload over `MaxPayloadSizeBytes` is not retried, since a retry cannot shrink it.

### Payload size

| Key | Default | Purpose |
|---|---|---|
| `MaxPayloadSizeBytes` | 5242880 (5 MB) | Ceiling on an HTTP body |
| `MaxRecordsPerBatch` | 1000 | Records per batch when batching |
| `EnablePayloadBatching` | true | Split large sets into batches |

Measured after compression. Over the limit fails to a dead letter rather than sending.

### Dead letters

| Key | Default | Purpose |
|---|---|---|
| `DeadletterRetentionDays` | 30 | Age at which rows are purged |
| `DeadLetterThreshold` | 100 | Total that triggers a warning |
| `DeadLetterCheckIntervalMinutes` | 30 | Monitor interval |
| `DeadLetterMonitorEnabled` | true | Enable the monitor |

Purge runs at startup and every 24 hours.

### Connection health

| Key | Default | Purpose |
|---|---|---|
| `HealthCheckEnabled` | true | Probe databases and queues |
| `HealthCheckIntervalMinutes` | 15 | Interval between probes |

First probe runs 10 seconds after startup so the dashboard has data on first load.

## ChangeTracking:StateDbPath

Where last processed versions are stored. Default `state.db` relative to the working directory. Deleting it re-initialises every object per its `InitialSyncMode`.

## Health

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | false | Expose [health endpoints](/reference/health) |
| `Port` | 2455 | Listening port, shared with the UI |
| `Host` | `*` | Binding host |
| `CacheDurationSeconds` | 120 | How long `/health` is cached |

`/health` opens a real connection per database, so caching stops a polling monitor from generating load.

## WebHost

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | false | Serve the dashboard |
| `Host` | `*` | `localhost` or `127.0.0.1` restricts `/ui` to loopback |
| `SecureCookies` | unset | Force the `Secure` cookie flag |

Unset, `SecureCookies` follows the request scheme. Set `true` behind a TLS-terminating proxy where the app sees plain HTTP.

## Trignis:AdminApiKey

The dashboard credential. Empty means the UI is unauthenticated.

::: danger
Ships as a placeholder. Change it before exposing the port.
:::

## Windows:UseEventLog

Windows only. Writes to the Application event log as source `Trignis` alongside the file log.

## Serilog

Standard [Serilog](https://github.com/serilog/serilog-settings-configuration) configuration. Default is console plus a daily rolling file in `log/`, five files retained.
