# Trignis

[![License](https://img.shields.io/badge/license-EUPL%201.2-blue)](LICENSE)
[![Last commit](https://img.shields.io/github/last-commit/melosso/trignis)](https://github.com/melosso/trignis/commits/main)
[![Latest Release](https://img.shields.io/github/v/release/melosso/trignis)](https://github.com/melosso/trignis/releases/latest)

Trignis watches your SQL Server tables and tells something else when they change. Point it at a database, say which objects to track and where changes should go, and it keeps sending them to a file, an HTTP endpoint, or a message queue.

Useful for data synchronization, audit trails, ETL processes, and application integration scenarios where you need to track and propagate database changes reliably.

![Screenshot of Trignis](https://github.com/melosso/trignis/blob/main/.github/images/screenshot.png?raw=true)

Trignis polls on a timer rather than firing on every write. That is intential. In some cases, the same record may be updated up to 50 times before the final commit. If you're looking for a direct or real-time mechanism, please consider implementing it programmatically through a SDK or using alternative extension methods.

## Requirements

- [.NET 10+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Microsoft SQL Server with change tracking enabled

> [!CAUTION]
> Change tracking accumulates side-table data that has to be cleaned up, and SQL Server 2025 changed how that cleanup behaves. Read [Risks](#risks) before enabling it on a production database.

## Getting started

### Docker

Configure `environments/` and `appsettings.json`, then start the container. The setup script does both:

```bash
sh -c "$(curl -fsSL https://raw.githubusercontent.com/melosso/trignis/refs/heads/main/docker-setup.sh)"
```

Prefer to do it by hand? Start from the included [docker-compose.yml](docker-compose.yml).

### Windows

Download the [latest release](https://github.com/melosso/trignis/releases) and extract it to your deployment folder.

Trignis encrypts its own configuration, so it needs a key before first run. On Windows, store it as a machine environment variable:

```powershell
$bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Environment]::SetEnvironmentVariable("TRIGNIS_ENCRYPTION_KEY", [Convert]::ToBase64String($bytes), "Machine")
```

In containers, set `TRIGNIS_ENCRYPTION_KEY` the same way. Lose this key and the encrypted configuration cannot be recovered. You would have to delete `.core/` and re-enter every secret.

### Point it at a database

Each file in `environments/` is one self-contained environment, with its own connection strings, tracked objects and destinations:

```json
{
  "ConnectionStrings": {
    "PrimaryDatabase": "Server=prod-sql.company.com;Database=PrimaryDB;Trusted_Connection=True;"
  },
  "ChangeTracking": {
    "ExportToFile": true,
    "TrackingObjects": [
      {
        "Name": "Orders",
        "Database": "PrimaryDatabase",
        "TableName": "dbo.Orders",
        "StoredProcedureName": "dbo.sp_GetOrderChanges"
      }
    ]
  }
}
```

Drop a file in and Trignis picks it up without a restart. Start the service, then browse to **[http://localhost:2455](http://localhost:2455)** for the dashboard.

Changes are read through a stored procedure you write, which keeps the shape of the payload under your control. That procedure has to return a specific JSON structure. See the Reference for the contract and a working example.

## Risks

Change tracking is not free. Expect roughly 5–10% slower writes on tracked tables, since every change is recorded. The side tables grow and need regular cleanup.

SQL Server 2025 (17.x) and later switched to an adaptive shallow cleanup for large side tables, which behaves differently from the deep cleanup in earlier versions. `sp_flush_CT_internal_table_on_demand` still has to be run manually. Trignis does not do it for you. See [About Change Tracking](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-tracking-sql-server?view=sql-server-ver17) and [KB3173157](https://support.microsoft.com/en-us/topic/kb3173157-adds-a-stored-procedure-for-the-manual-cleanup-of-the-change-tracking-side-table-in-sql-server-2fe76677-8687-acc0-12a9-78f3709fc621).

Failed exports go to a dead letter store (`sinkhole.db`) rather than being retried forever. They are not resent automatically. Replay them from the dashboard once the downstream problem is fixed.

Compatibility with older SQL Server versions is not guaranteed.

> [!TIP]
> Brent Ozar's [Performance Tuning SQL Server Change Tracking](https://www.brentozar.com/archive/2014/06/performance-tuning-sql-server-change-tracking/) is worth reading before committing to this.

## Lore

> Trignis blends *trigger* with *ignis*; Latin for fire, or spark. A trigger sets automation in motion, ignis is the moment it catches. Trignis meddles in exactly that moment.

## License

Licensed under the **EUPL 1.2**. See the [license](LICENSE) file for details.

## Contributing

Contributions are welcome. Please use the issue and pull request templates.
