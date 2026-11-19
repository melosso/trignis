TRIGNIS EXAMPLE FOLDER
======================

SQL Server scripts for Exact Globe and Exact Synergy tables. Melosso can't be held liable for any misuse or damages caused by these examples.

Each script performs these steps on the current database:

- Creates the `web` schema.
- Enables change tracking and snapshot isolation on the database.
- Enables change tracking on the table.
- Creates the sync procedure.

Each script is idempotent.

| Script | Table | Procedure | Tracking |
|---|---|---|---|
| `Exact Globe/column-tracking_dbo.Items.sql` | `dbo.Items` | `web.get_itemssync` | Column level, with `$changed` |
| `Exact Globe/table-tracking_dbo.pwruc.sql` | `dbo.pwruc` | `web.get_pwrucsync` | Row level |
| `Exact Synergy/table-tracking_dbo.HRRoles.sql` | `dbo.HRRoles` | `web.get_hrrolessync` | Row level |

An environment file registers a procedure under `tables:`:

```yaml
tables:
  - name: Items
    database: ExactGlobe
    table: dbo.Items
    procedure: web.get_itemssync
    initial_sync: incremental
```

`Tests/Trignis.Tests/Stress/ExampleProcedureTests.cs` runs every script on SQL Server 2022 (`Tests/parity/run.sh`).

Procedure contract: https://melosso.github.io/trignis/reference/stored-procedure
