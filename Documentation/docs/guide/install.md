---
title: Install
description: Run Trignis with Docker or on Windows
---

# Install

## Requirements

- [.NET 10+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Microsoft SQL Server, or PostgreSQL 13 or newer

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

## Prepare the database

Trignis reads what your database already tracks; it does not set any of it up for you. What that means depends on the provider.

### SQL Server

Change tracking is the usual choice, and has to be switched on per database and per table.

```sql
-- Database level
ALTER DATABASE YourDatabase
  SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

-- Per table
ALTER TABLE dbo.YourTable ENABLE CHANGE TRACKING;
```

If your procedures use snapshot isolation, which the [contract reference](/reference/stored-procedure#sql-server) recommends, it also needs allowing once per database:

```sql
ALTER DATABASE YourDatabase SET ALLOW_SNAPSHOT_ISOLATION ON;
```

`CHANGE_RETENTION` sets how long history is kept. If Trignis is offline longer than the retention window, the stored version becomes too old and the next run falls back to a full sync.

Change tracking is not required. An existing `rowversion` column works too, as long as you can report deletes some other way.

::: danger
Change tracking side tables grow and need cleanup, and SQL Server 2025 changed how automatic cleanup behaves. Read the [risks](https://github.com/melosso/trignis#risks) before enabling this on a production database.
:::

### PostgreSQL

There is no built-in change tracking to switch on. You supply the watermark, normally through a small outbox table fed by a trigger, which is also what makes deletes visible.

The [stored procedure contract](/reference/stored-procedure#postgresql) has the table, the trigger and a working function, along with the transaction-horizon rule that stops concurrent inserts from being skipped.

Set `"Provider": "postgres"` in the environment file. Omitted, it defaults to `mssql`.

## Verify

Start Trignis and open **http://localhost:2455**. The dashboard shows the environments it loaded and the objects it is tracking.
