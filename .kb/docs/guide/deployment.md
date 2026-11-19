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

The container listens on port 2455, and its built-in health check calls that port. To serve Trignis on another port, change the published port (`"8080:2455"`), not `Health:Port`.

The image keeps `state.db`, `sinkhole.db` and the `.core` key folder in `/app/data`. Mount a volume there, or an upgrade loses them:

- Without `state.db`, every object re-initialises according to `initial_sync`.
- Without `sinkhole.db`, unreplayed dead letters are gone.
- Without `.core`, Trignis cannot decrypt the secrets in your environment files.

::: warning Upgrading a container from v0.4
Up to v0.4 the image kept these files in `/app`, outside every volume. Copy them out before you replace the container:

```bash
mkdir -p data
docker cp trignis:/app/state.db data/
docker cp trignis:/app/sinkhole.db data/   # absent if there never was a dead letter
docker cp trignis:/app/.core data/
```

Then mount `./data:/app/data` (or a named volume holding these files) and start the new version.
:::

### Backup and restore

Back up the data folder and `TRIGNIS_ENCRYPTION_KEY` together. One without the other cannot decrypt your environment files.

The databases can be copied while Trignis runs:

```bash
docker exec trignis sqlite3 /app/data/state.db ".backup /app/data/state.bak"
docker exec trignis sqlite3 /app/data/sinkhole.db ".backup /app/data/sinkhole.bak"
```

To restore: stop Trignis, put the files back, start it. A `state.db` older than the last export makes Trignis send the changes since the backup again. Receivers handle this like any other [repeat delivery](/reference/delivery).

### High availability

Run one instance per data folder. Active/passive works: a standby with the same volume and key starts where the active one stopped. Two active instances on the same tables send every change twice.

### Pinning a version

Every release publishes four tags. Pick by how much movement you want:

| Tag | Moves when | Use for |
|---|---|---|
| `v0.4.0` | Never | Production |
| `0.4` | A patch release | Production, if you want fixes automatically |
| `latest` | Every release | Trying it out |
| `<commit sha>` | Never | Reproducing a specific build |

```yaml
image: ghcr.io/melosso/trignis:v0.4.0
```

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
`AdminApiKey` ships with a placeholder. Empty or unchanged means the UI is unauthenticated. Set a unique key before exposing the port, and put TLS in front of it. See [Authentication](/reference/authentication).
:::

## Logs

Written to `log/log-<date>.txt`, daily rolling, five files kept. Adjust under `Serilog` in `appsettings.json`. On Windows, `Windows.UseEventLog` also writes to the Application event log.
