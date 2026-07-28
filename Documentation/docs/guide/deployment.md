---
title: Deployment
description: Run Trignis as a Windows service or a container
---

# Deployment

## Windows service

Trignis registers itself as **Trignis (Agent)**. Install it with `sc.exe`, pointing at the extracted executable:

```powershell
sc.exe create "Trignis (Agent)" binPath= "C:\trignis\Trignis.exe" start= auto
sc.exe start "Trignis (Agent)"
```

The service account needs:

- Read/write on the install folder: `state.db`, `sinkhole.db`, `log/` and any export directory live there.
- Read on the `TRIGNIS_ENCRYPTION_KEY` machine variable.
- Whatever the connection strings need. With `Trusted_Connection=True`, that is the service account itself. `LocalSystem` will not do, so use an account the database recognises.

Shutdown is graceful: in-flight cycles get up to 30 seconds to finish before the host stops them.

## Docker

```yaml
services:
  trignis:
    image: ghcr.io/melosso/trignis:latest
    container_name: trignis
    ports:
      - "2455:2455"
    environment:
      TRIGNIS_ENCRYPTION_KEY: ${TRIGNIS_ENCRYPTION_KEY}
    volumes:
      - ./environments:/app/environments
      - ./appsettings.json:/app/appsettings.json
      - trignis-state:/app/data
volumes:
  trignis-state:
```

Persist `state.db` and `sinkhole.db`. Losing `state.db` means every object re-initialises on next start; losing `sinkhole.db` discards unreplayed dead letters.

## Selecting one environment

By default every file in `environments/` is loaded. To run one instance per environment, set `TRIGNIS_ENVIRONMENT` to a filename without its extension:

```bash
TRIGNIS_ENVIRONMENT=production
```

Unmatched names are logged and all environments load anyway, so a typo does not silently stop the service.

## Exposing the dashboard

`Health` and `WebHost` are separate switches on the same port:

```json
{
  "Health":  { "Enabled": true, "Port": 2455, "Host": "*" },
  "WebHost": { "Enabled": true, "Host": "*" },
  "Trignis": { "AdminApiKey": "change-me" }
}
```

- `Health.Enabled`: the JSON [health endpoints](/reference/health).
- `WebHost.Enabled`: the dashboard UI.
- `WebHost.Host` set to `localhost` or `127.0.0.1` restricts `/ui` to loopback; anything else is refused with 403.

::: danger
`AdminApiKey` ships with a placeholder. Empty or unchanged means the UI is unauthenticated. Set a real key before exposing the port, and put TLS in front of it. See [Authentication](/reference/authentication).
:::

## Logs

Written to `log/log-<date>.txt`, daily rolling, five files kept. Adjust under `Serilog` in `appsettings.json`. On Windows, `Windows.UseEventLog` also writes to the Application event log.
