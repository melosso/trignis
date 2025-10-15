# Trignis

[![License](https://img.shields.io/badge/license-AGPL%203.0-blue)](LICENSE)
[![Last commit](https://img.shields.io/github/last-commit/melosso/trignis)](https://github.com/melosso/trignis/commits/main)
[![Latest Release](https://img.shields.io/github/v/release/melosso/trignis)](https://github.com/melosso/trignis/releases/latest)

**Trignis** is a high-performance change tracking service for SQL Server databases. It monitors database changes in real-time, processes them efficiently, and exports data to either files - or external API's.

Useful for data synchronization, audit trails, ETL processes, and integration scenarios where you need to track and propagate database changes reliably.

A quick example of Trignis in action:

![Screenshot of Trignis](https://github.com/melosso/trignis/blob/main/.github/images/screenshot.png?raw=true)

We've chosen to use a timed propagation mechanism due to various legacy applications that may handle database updates independently. In some cases, the same record may be updated up to 50 times before the final commit. If you're looking for a direct or real-time mechanism, please consider implementing it programmatically through a SDK or alternative extension methods.

## 🧩 Key Features

This application is designed for reliability and performance, predominantly in Windows Server environments. It offers flexible change tracking, versatile export options, and comprehensive security.

* **Real-time change tracking**: Monitors SQL Server change tracking tables and processes updates automatically
* **Multiple export destinations**: Export changes to JSON files or REST APIs
* **Built-in encryption**: Secure configuration files with modern encryption standards
* **Environment-aware**: Isolated configurations for dev/staging/prod environments
* **Comprehensive logging**: Detailed request/response tracing with Serilog
* **Windows service support**: Runs as a background service with proper lifecycle management
* **State persistence**: Uses SQLite to track processing state and avoid duplicates
* **Configurable polling**: Adjustable polling intervals and retry policies
* **File management**: Automatic cleanup of old export files with size limits

## ⚙️ Requirements

Before deploying **Trignis**, ensure your environment meets the following requirements:

- [.NET 9+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- **SQL Server** with *Change Tracking* enabled (we’ll walk you through this later)
- **Windows Server** (for hosting the background service)
- **Database and filesystem access** for configuration and logging


## 🚀 Getting Started

Follow these steps to get Trignis running in your environment.

### 1. Download & Extract

Download the [latest release](https://github.com/melosso/trignis/releases) and extract to your deployment folder.

### 2. Configure Environments

Set up environment-specific configurations in the `environments/` folder.

**`environments/example.json`**

```json
{
  "ConnectionStrings": {
    "PrimaryDatabase": "Server=localhost;Database=PrimaryDB;Trusted_Connection=True;",
    "SecondaryDatabase": "Server=localhost;Database=SecondaryDB;Trusted_Connection=True;"
  },
  "ChangeTracking": {
    "TrackingObjects": [
      {
        "Name": "Customers",
        "Database": "PrimaryDatabase",
        "TableName": "dbo.Customers",
        "StoredProcedureName": "sp_GetCustomerChanges"
      }
    ],
    "PollingIntervalSeconds": 30,
    "ExportToFile": true,
    "ExportToApi": false,
    "FilePath": "exports/{object}/{database}/changes-{timestamp}.json",
    "FilePathSizeLimit": 500,
    "RetryCount": 3,
    "RetryDelaySeconds": 5
  }
}
```

### 3. Enable Change Tracking

Ensure change tracking is enabled on your SQL Server databases:

```sql
-- Enable change tracking at database level
ALTER DATABASE YourDatabase SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

-- Enable change tracking on tables
ALTER TABLE dbo.YourTable ENABLE CHANGE_TRACKING;
```

### 4. Configure

To configure Trignis for data retrieval, you need to create a stored procedure that leverages SQL Server change tracking to fetch changes from your tables. This procedure should handle both full synchronization (when `fromVersion` is 0) and incremental changes (diff sync).

For example, for the table `dbo.Items` with columns `ItemCode`, `Description`, `Assortment`, and `sysguid`, you can create a procedure like shown here below.

> [!TIP]
> You can choose between table or [column](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/work-with-change-tracking-sql-server?view=sql-server-ver17#use-column-tracking) tracking. The code snippet herebelow will shown both:

```sql
-- Table change tracking
CREATE OR ALTER PROCEDURE web.get_itemssync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion INT = JSON_VALUE(@json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;

        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Items'));

        IF (@fromVersion = 0)
        BEGIN
            SET @reason = 0; -- First Sync
        END
        ELSE IF (@fromVersion < @minVer)
        BEGIN
            SET @fromVersion = 0;
            SET @reason = 1; -- fromVersion too old. New full sync needed
        END

        IF (@fromVersion = 0)
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Full' AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT ItemCode, Description, Assortment, sysguid
                    FROM dbo.Items
                    FOR JSON AUTO
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
        ELSE
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Diff' AS 'Metadata.Sync.Type',
                [Data] = JSON_QUERY((
                    SELECT
                        ct.SYS_CHANGE_OPERATION AS '$operation',
                        ct.SYS_CHANGE_VERSION AS '$version',
                        ct.ItemCode,
                        i.Description,
                        i.Assortment,
                        i.sysguid
                    FROM dbo.Items AS i
                    RIGHT OUTER JOIN CHANGETABLE(CHANGES dbo.Items, @fromVersion) AS ct
                        ON ct.ItemCode = i.ItemCode
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END

    COMMIT TRAN;
END
```

```sql
-- Column (specific) change tracking
CREATE OR ALTER PROCEDURE web.get_itemssync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion INT = JSON_VALUE(@json, '$.fromVersion');

    -- Column IDs for change tracking (used to check which columns changed in updates)
    DECLARE @Description INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Description', 'ColumnId');
    DECLARE @Assortment INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Assortment', 'ColumnId');
    DECLARE @Sysguid INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'sysguid', 'ColumnId');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;

        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Items'));

        IF (@fromVersion = 0)
        BEGIN
            SET @reason = 0; -- First Sync
        END
        ELSE IF (@fromVersion < @minVer)
        BEGIN
            SET @fromVersion = 0;
            SET @reason = 1; -- fromVersion too old. New full sync needed
        END

        IF (@fromVersion = 0)
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Full' AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT ItemCode, Description, Assortment, sysguid
                    FROM dbo.Items
                    FOR JSON AUTO
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
        ELSE
		BEGIN
			SELECT
				@curVer AS 'Metadata.Sync.Version',
				'Diff' AS 'Metadata.Sync.Type',
				[Data] = JSON_QUERY((
					SELECT
						ct.SYS_CHANGE_OPERATION AS '$operation',
						ct.SYS_CHANGE_VERSION AS '$version',
						ct.ItemCode,
						CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Description, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.Description ELSE NULL END AS Description,
						CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Assortment, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.Assortment ELSE NULL END AS Assortment,
						CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Sysguid, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.sysguid ELSE NULL END AS sysguid
					FROM dbo.Items AS i
					RIGHT OUTER JOIN CHANGETABLE(CHANGES dbo.Items, @fromVersion) AS ct
						ON ct.ItemCode = i.ItemCode
					WHERE ct.SYS_CHANGE_OPERATION != 'U'  -- Include all inserts and deletes
					   OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Description, ct.SYS_CHANGE_COLUMNS) = 1
					   OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Assortment, ct.SYS_CHANGE_COLUMNS) = 1
					   OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Sysguid, ct.SYS_CHANGE_COLUMNS) = 1
					FOR JSON PATH
				))
			FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
		END

    COMMIT TRAN;
END
```

Then, reference this procedure in your environment configuration under `StoredProcedureName`.

### 5. Deploy as Windows Service

Before continuing, make sure you've set-up everything. On initial run, all secrets will be stored safely (meaning: they'll be hashed). For production deployment:

1. Run `TrignisBackgroundService.bat install` as Administrator
2. Run `TrignisBackgroundService.bat start` to start the service
3. Check the log output to make sure you've succesefully configured Trignis.

## 🔐 Security & Encryption

### Configuration Encryption

Trignis automatically encrypts sensitive configuration data using RSA+AES hybrid encryption. Both the database connection strings and any API-security information are stored safely.

### Authentication

When exporting to APIs, configure authentication in your environment files:

```json
{
  "ChangeTracking": {
    "ExportToApi": true,
    "ApiEndpoints": [
      {
        "Key": "production_webhook1",
        "Url": "http://portway/v1/api/prod/Webhook/webhook1",
        "Auth": {
          "Type": "Bearer",
          "Token": "<my_token_here>"
        }
      }
    ]
}
```

Supported auth types: `Bearer`, `Basic`, `APIKey`.

## 📡 Usage Examples

### Monitoring Changes

Trignis automatically monitors configured tables and exports changes:

```json
// Example exported change file
{
  "timestamp": "2025-10-14T10:30:00Z",
  "object": "Items",
  "database": "PrimaryDatabase",
  "changes": [
    {
      "operation": "INSERT",
      "primaryKey": "ABC123",
      "data": {
        "ItemCode": "ABC123",
        "Description": "Sample Item Description",
        "Assortment": "Electronics",
        "sysguid": "550e8400-e29b-41d4-a716-446655440000"
      }
    }
  ]
}
```

### API Export

Configure webhook-style exports:

```json
{
  "ChangeTracking": {
    "ExportToApi": true,
    "ApiEndpoints": [
      {
        "Key": "production_webhook1",
        "Url": "http://portway/v1/api/prod/Webhook/webhook1",
        "Auth": {
          "Type": "Bearer",
          "Token": "<my_token_here>"
        }
      }
    ]
  }
}
```

## 🔄 Compatibility

Compatibility with all SQL Server versions is **not** guaranteed. This application may not work at all on deprecated versions of Microsoft SQL Server.

Note that SQL Server 2025 introduced changes to change tracking autocleanup. In SQL Server 2025 (17.x) Preview and later versions, the autocleanup process introduces an adaptive shallow cleanup approach for large side tables, which is enabled by default. 

This differs from the deep cleanup in earlier versions. Note that the `sp_flush_CT_internal_table_on_demand [@TableToClean =] 'tablename'` procedure must be handled manually outside of Trignis.

For more details, refer to [About Change Tracking (SQL Server)](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-tracking-sql-server?view=sql-server-ver17) and [KB3173157](https://support.microsoft.com/en-us/topic/kb3173157-adds-a-stored-procedure-for-the-manual-cleanup-of-the-change-tracking-side-table-in-sql-server-2fe76677-8687-acc0-12a9-78f3709fc621).

> [!CAUTION]
> Before installing Trignis (or any change tracking application that leverages the same mechanism), make sure that you've read and understood these **cleanup** procedures. Failing to do so may result in data loss, degraded performance or incorrect change tracking behavior.

## 📊 Logging & Monitoring

Trignis provides comprehensive logging:

* Application logs: `log/trignis-.log` (daily rotation)
* Configuration status on startup
* Change processing details
* Error handling with retry logic

> [!TIP]
> If the application isn't starting, consider changing value `UseEventLog` in the `appsettings.json` configuration file to true. To prevent your Windows Event Viewer from bloating, make sure to disable when you're done troubleshooting.

## 🔮 Lore

The name Trignis is derived from Trigger and Ignis, Latin for fire or spark respectively. In databases, a trigger automates actions in response to specific events. Combined with Ignis, it captures the essence of a spark that ignites automation. Trignis represents the moment where automation starts.

## 🤝 Credits

Built with:

* [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
* [Dapper](https://github.com/DapperLib/Dapper)
* [Serilog](https://serilog.net/)
* [SQLite](https://www.sqlite.org/)
* [Polly](https://github.com/App-vNext/Polly)

## License

Free for open source projects and personal use under the **AGPL 3.0** license. For more information, please see the [license](LICENSE) file.

## Contributing

Contributions welcome! Please submit issues and pull requests.