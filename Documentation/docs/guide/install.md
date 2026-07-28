---
title: Install
description: Run Trignis with Docker or on Windows
---

# Install

## Requirements

- [.NET 10+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Microsoft SQL Server with change tracking enabled

## The encryption key

Trignis encrypts connection strings and credentials in its configuration files. It needs a key before the first run, supplied as `TRIGNIS_ENCRYPTION_KEY`.

On Windows, store it as a machine environment variable:

```powershell
$bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Environment]::SetEnvironmentVariable("TRIGNIS_ENCRYPTION_KEY", [Convert]::ToBase64String($bytes), "Machine")
```

In containers, pass the same variable through your compose file or secret store.

::: warning
Back this key up. If it is lost or changed, the existing encrypted configuration cannot be decrypted. You have to delete the `.core/` folder and re-enter every secret.

Without the variable set, Trignis falls back to a built-in key and logs a warning. That is fine for a local trial and unacceptable in production.
:::

## Docker

The setup script prepares `environments/` and `appsettings.json`, then starts the container:

```bash
sh -c "$(curl -fsSL https://raw.githubusercontent.com/melosso/trignis/refs/heads/main/docker-setup.sh)"
```

To do it by hand, start from the [docker-compose.yml](https://github.com/melosso/trignis/blob/main/docker-compose.yml) in the repository.

## Windows

1. Download the [latest release](https://github.com/melosso/trignis/releases) and extract it to your deployment folder.
2. Set `TRIGNIS_ENCRYPTION_KEY` as above.
3. Run the executable once to confirm it starts, then [install it as a service](/guide/deployment).

## Enable change tracking in SQL Server

Trignis reads change tracking; it does not turn it on for you.

```sql
-- Database level
ALTER DATABASE YourDatabase
  SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

-- Per table
ALTER TABLE dbo.YourTable ENABLE CHANGE TRACKING;
```

`CHANGE_RETENTION` sets how long history is kept. If Trignis is offline longer than the retention window, the stored version becomes too old and the next run falls back to a full sync.

::: danger
Change tracking side tables grow and need cleanup, and SQL Server 2025 changed how automatic cleanup behaves. Read the [risks](https://github.com/melosso/trignis#risks) before enabling this on a production database.
:::

## Verify

Start Trignis and open **http://localhost:2455**. The dashboard shows the environments it loaded and the objects it is tracking.
