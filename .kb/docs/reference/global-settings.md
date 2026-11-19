---
title: Global settings
description: Every appsettings.json key
---

# Global settings

`appsettings.json` contains the settings that apply to your whole installation: how often Trignis polls, what it does when an export fails, whether the dashboard is served at all. Unlike the environment files, this one is read once when the service starts, so a change here needs a restart before it takes effect.

Settings can also come from environment variables and a `.env` file; see [Sources](#sources) below.

Here is the whole file with its defaults, so you can see the shape before working through the sections below:

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
      "DeadLetterAutoReplayEnabled": true,
      "DeadLetterReplayIntervalSeconds": 60,
      "DeadLetterMaxReplayAttempts": 5,
      "DeadLetterReplayBackoffSeconds": 60,
      "HealthCheckEnabled": true,
      "HealthCheckIntervalMinutes": 15,
      "MaxPayloadSizeBytes": 5242880,
      "MaxRecordsPerBatch": 1000,
      "EnablePayloadBatching": true
    }
  }
}
```

You are welcome to leave most of this alone. Every key has a working default, and a minimal `appsettings.json` with just `AdminApiKey` and `WebHost` will get you running.

## ChangeTracking:GlobalSettings

These are the defaults every environment inherits. Six of them can be overridden by an individual [environment file](/reference/environment) when one environment needs to behave differently from the rest; the others stay global. The **Per-env** column tells you which is which.

### Polling and export

| Key | Default | Purpose | Per-env |
|---|---|---|---|
| `PollingIntervalSeconds` | 30 | Seconds between cycles | yes |
| `ExportToFile` | false | Write to disk | yes |
| `FilePath` | `exports/{environment}/...` | Output template | yes |
| `FilePathSizeLimit` | 500 | Export directory cap in MB | no |
| `ExportToApi` | false | Send to endpoints | yes |

Trignis will log a friendly warning if you set the polling interval below 5 seconds or above an hour, since both tend to be accidents. Neither is rejected, though, so if you have a good reason for a 2 second interval it is yours to make.

### Retries

| Key | Default | Purpose | Per-env |
|---|---|---|---|
| `RetryCount` | 3 | Attempts before a dead letter | yes |
| `RetryDelaySeconds` | 5 | Fixed delay between attempts | yes |

The delay here is fixed rather than exponential, which suits the transient failures these retries are meant for: a transport hiccup, a brief IO error, a database that was momentarily unreachable. The [dead letter replay](/guide/dead-letters) retries longer outages with backoff.

One thing worth knowing: a payload larger than `MaxPayloadSizeBytes` is not retried, because a second attempt cannot make it smaller. It goes straight to the dead letter store instead.

### Payload size

| Key | Default | Purpose |
|---|---|---|
| `MaxPayloadSizeBytes` | 5242880 (5 MB) | Ceiling on an HTTP body |
| `MaxRecordsPerBatch` | 1000 | Records per batch when batching |
| `EnablePayloadBatching` | true | Split large sets into batches |

The size is measured after compression, so enabling gzip on an endpoint genuinely buys you headroom. Anything still over the limit fails to a dead letter rather than being sent, which keeps a receiving API from having to reject it for you.

### Dead letters

| Key | Default | Purpose |
|---|---|---|
| `DeadletterRetentionDays` | 30 | Age at which rows are purged |
| `DeadLetterThreshold` | 100 | Total that triggers a warning |
| `DeadLetterCheckIntervalMinutes` | 30 | Monitor interval |
| `DeadLetterMonitorEnabled` | true | Enable the monitor |
| `DeadLetterAutoReplayEnabled` | true | Retry dead letters automatically |
| `DeadLetterReplayIntervalSeconds` | 60 | Seconds between replay sweeps |
| `DeadLetterMaxReplayAttempts` | 5 | Attempts before a row waits for a human. 0 disables replay |
| `DeadLetterReplayBackoffSeconds` | 60 | First backoff delay, doubling per attempt, capped at 6 hours |

Purging runs when the service starts and every 24 hours after that. Since retention is a deletion policy, it is worth replaying anything you care about before it ages out. The [dead letters guide](/guide/dead-letters) walks through how the automatic retries and the manual ones fit together.

### Connection health

| Key | Default | Purpose |
|---|---|---|
| `HealthCheckEnabled` | true | Probe databases and queues |
| `HealthCheckIntervalMinutes` | 15 | Interval between probes |

The first probe runs about ten seconds after startup, which means the dashboard already has something to show you the first time you open it rather than an empty panel.

## ChangeTracking:DataPath

The folder for the files Trignis must keep: `state.db`, `sinkhole.db` and the `.core` key folder. Empty by default, which keeps them in the install folder. The container image sets it to `/app/data`; mount a volume there.

## ChangeTracking:StateDbPath

The state database stores the last processed version for each object. Defaults to `state.db` in the data path. A relative path resolves inside `DataPath`. It is also where a [pause](/guide/dashboard#pausing-change-tracking) is recorded.

Deleting the file is a valid way to start over: every object re-initialises according to its `InitialSyncMode` on the next cycle. Just be aware that an object set to `Full` will re-export its entire table when it does.

## Health

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | false | Expose [health endpoints](/reference/health) |
| `Port` | 2455 | Listening port, shared with the UI. In Docker, keep 2455 and change the published port instead |
| `Host` | `*` | Binding host |
| `CacheDurationSeconds` | 120 | How long `/health` is cached |

`/health` opens a connection to each configured database. The response is cached for `CacheDurationSeconds`. Frequent polling does not add database load.

## WebHost

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | false | Serve the dashboard |
| `Host` | `*` | `localhost` or `127.0.0.1` restricts `/ui` to loopback |
| `SecureCookies` | unset | Force the `Secure` cookie flag |
| `TrustedProxies` | loopback | Reverse proxy addresses or CIDR ranges |

If you would rather reach the dashboard through an SSH tunnel than expose it, setting `Host` to `127.0.0.1` is a tidy way to do that.

Left unset, `SecureCookies` follows the scheme of the incoming request. Set it to `true` for a TLS-terminating proxy that sends no `X-Forwarded-Proto` header. Without that header, Trignis receives plain HTTP and treats the connection as insecure.

`TrustedProxies` contains reverse proxy addresses or CIDR ranges, for example `["172.18.0.0/16"]`. Trignis reads the client address and scheme from `X-Forwarded-For` and `X-Forwarded-Proto` on requests from these proxies and from loopback. Trignis ignores these headers from other senders. The login lockout and the `localhost` restriction use the forwarded client address.

If a reverse proxy serves the dashboard: set `Host` to `*`. With `Host` set to `localhost`, Trignis rejects proxied requests from remote clients.

## Trignis:AdminApiKey

The credential for signing in to the dashboard, and the passphrase the UI asks for again before you [pause an environment](/guide/dashboard#pausing-change-tracking). Leaving it empty turns authentication off entirely, which is convenient on a laptop and unwise anywhere else.

::: danger
Trignis ships with a placeholder value here. Please change it before the port is reachable by anyone else.
:::

## Windows:UseEventLog

Windows only. Turning this on writes to the Application event log under the source `Trignis`, alongside the normal file log rather than instead of it. Handy when your monitoring already watches the event log.

## Serilog

Standard [Serilog](https://github.com/serilog/serilog-settings-configuration) configuration, so anything you already know about configuring Serilog applies here unchanged. Out of the box you get console output plus a daily rolling file in `log/`, with five files retained.

## Sources

Precedence, highest first:

1. Environment variables
2. `.env` in the install folder
3. `appsettings.{Environment}.json`
4. `appsettings.json`

Each setting accepts four environment variable names. If more than one name is set, the lowest number applies.

| Style | Example | Precedence |
|---|---|---|
| Canonical | `Health__Port` | 1 |
| Prefixed screaming snake | `TRIGNIS_HEALTH_PORT` | 2 |
| Screaming snake | `HEALTH_PORT` | 3 |
| Bare | `HealthPort` | 4 |

Unprefixed names the .NET host reads itself (`PORT`, `URLS`, `ASPNETCORE_*`, `DOTNET_*`) are not accepted.

::: warning
Use one spelling per setting. Name precedence applies after sources merge: `HEALTH_PORT` from `.env` overrides `Health:Port` in `appsettings.json`.
:::

| Setting | Prefixed name |
|---|---|
| `Trignis:AdminApiKey` | `TRIGNIS_ADMIN_API_KEY` |
| Encryption key (no `appsettings.json` key) | `TRIGNIS_ENCRYPTION_KEY` |
| `SelectedEnvironment` | `TRIGNIS_SELECTED_ENVIRONMENT`; `TRIGNIS_ENVIRONMENT` also works |
| `Health:Enabled`, `Health:Port`, `Health:Host`, `Health:CacheDurationSeconds` | `TRIGNIS_HEALTH_ENABLED`, `TRIGNIS_HEALTH_PORT`, `TRIGNIS_HEALTH_HOST`, `TRIGNIS_HEALTH_CACHE_DURATION_SECONDS` |
| `WebHost:Enabled`, `WebHost:Host` | `TRIGNIS_WEB_HOST_ENABLED`, `TRIGNIS_WEB_HOST_HOST` |
| `Windows:UseEventLog` | `TRIGNIS_USE_EVENT_LOG` |
| `ChangeTracking:DataPath`, `ChangeTracking:StateDbPath` | `TRIGNIS_DATA_PATH`, `TRIGNIS_STATE_DB_PATH` |
| `ChangeTracking:GlobalSettings:<Name>` | `TRIGNIS_` plus the name in screaming snake case, for example `TRIGNIS_POLLING_INTERVAL_SECONDS` |
