---
title: Global settings
description: Every appsettings.json key
---

# Global settings

`appsettings.json` holds the settings that apply to your whole installation: how often Trignis polls, what it does when an export fails, whether the dashboard is served at all. Unlike the environment files, this one is read once when the service starts, so a change here needs a restart before it takes effect.

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

The delay here is fixed rather than exponential, which suits the transient failures these retries are meant for: a transport hiccup, a brief IO error, a database that was momentarily unreachable. Longer outages are better handled by the [dead letter replay](/guide/dead-letters), which does back off.

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

## ChangeTracking:StateDbPath

This is where the last processed version for each object lives, defaulting to `state.db` next to the working directory. It is also where a [pause](/guide/dashboard#pausing-change-tracking) is recorded.

Deleting the file is a valid way to start over: every object re-initialises according to its `InitialSyncMode` on the next cycle. Just be aware that an object set to `Full` will re-export its entire table when it does.

## Health

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | false | Expose [health endpoints](/reference/health) |
| `Port` | 2455 | Listening port, shared with the UI |
| `Host` | `*` | Binding host |
| `CacheDurationSeconds` | 120 | How long `/health` is cached |

`/health` opens a real connection to each configured database rather than reporting a cached guess, which is what makes it trustworthy. That honesty has a cost, so the response is cached: a monitoring system polling every few seconds will not turn your health check into a load generator.

## WebHost

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | false | Serve the dashboard |
| `Host` | `*` | `localhost` or `127.0.0.1` restricts `/ui` to loopback |
| `SecureCookies` | unset | Force the `Secure` cookie flag |

If you would rather reach the dashboard through an SSH tunnel than expose it, setting `Host` to `127.0.0.1` is a tidy way to do that.

Left unset, `SecureCookies` follows the scheme of the incoming request. Setting it to `true` is the right call behind a TLS-terminating proxy, where the app itself only ever sees plain HTTP and would otherwise decide the connection is insecure.

## Trignis:AdminApiKey

The credential for signing in to the dashboard, and the passphrase the UI asks for again before you [pause an environment](/guide/dashboard#pausing-change-tracking). Leaving it empty turns authentication off entirely, which is convenient on a laptop and unwise anywhere else.

::: danger
Trignis ships with a placeholder value here. Please change it before the port is reachable by anyone else.
:::

## Windows:UseEventLog

Windows only. Turning this on writes to the Application event log under the source `Trignis`, alongside the normal file log rather than instead of it. Handy when your monitoring already watches the event log.

## Serilog

Standard [Serilog](https://github.com/serilog/serilog-settings-configuration) configuration, so anything you already know about configuring Serilog applies here unchanged. Out of the box you get console output plus a daily rolling file in `log/`, with five files retained.
