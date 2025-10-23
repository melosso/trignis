# Trignis

[![License](https://img.shields.io/badge/license-AGPL%203.0-blue)](LICENSE)
[![Last commit](https://img.shields.io/github/last-commit/melosso/trignis)](https://github.com/melosso/trignis/commits/main)
[![Latest Release](https://img.shields.io/github/v/release/melosso/trignis)](https://github.com/melosso/trignis/releases/latest)

**Trignis** is a high-performance change tracking service for SQL Server databases. It monitors database changes in real-time, processes them efficiently, and exports data to either files - or external API's.

Useful for data synchronization, audit trails, ETL processes, and integration scenarios where you need to track and propagate database changes reliably.

A quick example of Trignis in action:

![Screenshot of Trignis](https://github.com/melosso/trignis/blob/main/.github/images/screenshot.png?raw=true)

We've chosen to use a timed propagation mechanism due to various legacy applications that may handle database updates independently. In some cases, the same record may be updated up to 50 times before the final commit. If you're looking for a direct or real-time mechanism, please consider implementing it programmatically through a SDK or using alternative extension methods.

## 🧩 Key Features

Designed for reliability and performance in Windows Server environments, with flexible change tracking, versatile exports, and robust security.

* **Real-time change tracking** – Monitors SQL Server changes automatically.
* **Flexible exports** – JSON, REST APIs, or message queues (RabbitMQ, Azure Service Bus, AWS SQS).
* **Multi-environment support** – Separate dev/staging/prod configurations and processing threads.
* **Large payload handling** – Automatic batching for thousands of records.
* **Secure & auditable** – Encrypted configs, detailed logging, and state persistence via SQLite.
* **Service-ready** – Runs as a Windows service with configurable polling, retries, and file cleanup.
* **Health monitoring** – Built-in endpoints for service status, state, and dead-letter tracking.

## ⚙️ Requirements

Before deploying **Trignis**, ensure your environment meets the following requirements:

- [.NET 9+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
- **SQL Server** with *Change Tracking* enabled (we'll walk you through this later)
- **Windows Server** (for hosting the background service)
- **Database and filesystem access** for configuration and logging

## 🚀 Getting Started

Follow these steps to get Trignis running in your environment.

### 1. Download & Extract

Choose your deployment environment before continuing:

#### Windows (preferred)

Download the [latest release](https://github.com/melosso/trignis/releases) and extract to your deployment folder. 

#### Docker Compose

Before running with Docker Compose, you must configure the `environment/` and `appsettings.json` files before starting the container. You can do this in one-click, by running the following command:

```bash
sh -c "$(curl -fsSL https://raw.githubusercontent.com/melosso/trignis/refs/heads/main/docker-setup.sh)"
```

Prefer the manual method? Use the [docker-compose.yml](docker-compose.yml) example file we have prepared.

### 2. Configure Environments

Set up environment-specific configurations in the `environments/` folder. Each environment file is completely self-contained with its own connection strings, tracking objects, and API endpoints.

**`environments/production.json`**

```json
{
  "ConnectionStrings": {
    "PrimaryDatabase": "Server=prod-sql.company.com;Database=PrimaryDB;Trusted_Connection=True;",
    "SecondaryDatabase": "Server=prod-sql.company.com;Database=SecondaryDB;Trusted_Connection=True;"
  },
  "ChangeTracking": {
    "TrackingObjects": [
      {
        "Name": "Customers",
        "Database": "PrimaryDatabase",
        "TableName": "dbo.Customers",
        "StoredProcedureName": "sp_GetCustomerChanges",
        "InitialSyncMode": "Incremental"
      }
    ],
    "ApiEndpoints": [
      {
        "Key": "production_webhook",
        "Url": "https://api.company.com/webhooks/changes",
        "Auth": {
          "Type": "Bearer",
          "Token": "<token_here>"
        }
      }
    ],
    "PollingIntervalSeconds": 60,
    "ExportToFile": true,
    "ExportToApi": true
  }
}
```

**`environments/development.json`**

```json
{
  "ConnectionStrings": {
    "TestDatabase": "Server=localhost;Database=TestDB;Trusted_Connection=True;"
  },
  "ChangeTracking": {
    "TrackingObjects": [
      {
        "Name": "TestCustomers",
        "Database": "TestDatabase",
        "TableName": "dbo.Customers",
        "StoredProcedureName": "sp_GetCustomerChanges",
        "InitialSyncMode": "Full"
      }
    ],
    "ApiEndpoints": [
      {
        "Key": "dev_webhook",
        "Url": "http://localhost:5000/api/webhooks/changes"
      }
    ],
    "PollingIntervalSeconds": 15
  }
}
```

Each environment runs independently in its own thread, allowing different polling intervals and configurations without interference.

> [!TIP] 
> You can determine if you'd like to send all data (e.g. for propagation) or only the changed data. Switch the property `InitialSyncMode` between `"Full"` to send all existing data on first run, or `"Incremental"` (default) to start from the current change tracking version without sending data.

### 3. Configure Global Settings

Set application-wide defaults in `appsettings.json`:

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "PollingIntervalSeconds": 30,
      "MaxRecordsPerBatch": 1000,
      "EnablePayloadBatching": true,
      "DeadLetterThreshold": 100,
      "HealthCheckEnabled": true
    }
  },
  "Health": {
    "Enabled": true,
    "Port": 2455
  }
}
```

Environments can override these global settings as needed.

### 4. Enable Change Tracking

Ensure change tracking is enabled on your SQL Server databases:

```sql
-- Enable change tracking at database level
ALTER DATABASE YourDatabase SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

-- Enable change tracking on tables
ALTER TABLE dbo.YourTable ENABLE CHANGE TRACKING;
```

### 5. Configure Stored Procedures

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

### 6. Prepare your Deployment

If you're choosing to deploy the services on Windows, please make sure to prepare your environment: you'll need to safely store the application encryption key. On containerized environments, this can be done with the identically named `TRIGNIS_ENCRYPTION_KEY` variable.

```powershell
# Immediately stores the encryption key to your System Environment Variables
$bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Environment]::SetEnvironmentVariable("TRIGNIS_ENCRYPTION_KEY", [Convert]::ToBase64String($bytes), "Machine")
```

### 7. Deploy as Windows Service

Before continuing, make sure you've set-up everything. On initial run, all secrets will be stored safely (meaning: they'll be hashed). For production deployment:

1. Run `TrignisBackgroundService.bat install` as Administrator
2. Run `TrignisBackgroundService.bat start` to start the service
3. Check the log output to make sure you've successfully configured Trignis.

> [!TIP]
> If you'd like to verify if you have succesfully configured Trignis, you can run `TrignisBackgroundService.bat test` to run the application in the console instead. 

## 📑 Output Structure

The change tracking mechanism (in the database) relies on a **consistent** JSON output structure from the configured stored procedures. Each procedure must return a JSON object with the following format:

```json
{
  "Metadata": {
    "Sync": {
      "Version": 12345,
      "Type": "Full|Diff",
      "ReasonCode": 0
    }
  },
  "Data": [
    {
      "$operation": "INSERT|UPDATE|DELETE",
      "$version": 12345,
      "primaryKey": "value",
      "data": {
        "field1": "value1",
        "field2": "value2"
      }
    }
  ]
}
```

- **Metadata.Sync.Version**: The current change tracking version (integer).
- **Metadata.Sync.Type**: Synchronization type ("Full" for initial sync, "Diff" for incremental changes).
- **Metadata.Sync.ReasonCode**: Reason code (e.g., 0 for first sync, 1 if fromVersion is too old).
- **Data**: Array of change records. Each record includes:
  - `$operation`: The operation type.
  - `$version`: The version of the change.
  - Primary key and data fields (structure depends on table schema and tracking mode).

Inconsistent or malformed JSON will cause processing failures. Ensure stored procedures adhere to this structure for reliable change detection and export. For column-level tracking, unchanged fields may be `null` in updates. In other words, prevent using the `INCLUDE_NULL_VALUES` when using column tracking.

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
        "Key": "PowerAutomate",
        "Url": "https://your-powerautomate-webhook-url",
        "Auth": {
          "Type": "Bearer",
          "Token": "your-token-here"
        }
      }
    ]
}
```

Supported auth types: `Bearer`, `Basic`, `ApiKey`, `OAuth2ClientCredentials`.

## 📡 Usage Examples

### Monitoring Changes

Trignis automatically monitors configured tables and exports changes:

```json
// Example exported change file
{
  "Metadata": {
    "Sync": {
      "Version": 12345,
      "Type": "Diff",
      "ReasonCode": 0
    }
  },
  "Data": [
    {
      "$operation": "INSERT",
      "$version": 12345,
      "ItemCode": "ABC123",
      "Description": "Sample Item",
      "Assortment": "Electronics",
      "sysguid": "550e8400-e29b-41d4-a716-446655440000"
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
        "Url": "http://portway/v1/api/prod/Webhook/{object}",
        "Auth": {
          "Type": "Bearer",
          "Token": "<my_token_here>"
        }
      }
    ]
  }
}
```

### OAuth 2.0 Client Credentials

Automatically manage token acquisition and renewal for OAuth-protected APIs:

```json
{
  "ChangeTracking": {
    "ApiEndpoints": [
      {
        "Key": "oauth_api",
        "Url": "https://api.example.com/webhook",
        "Auth": {
          "Type": "OAuth2ClientCredentials",
          "TokenEndpoint": "https://auth.example.com/oauth/token",
          "ClientId": "your-client-id",
          "ClientSecret": "your-client-secret",
          "Scope": "api.write"
        }
      }
    ]
  }
}
```

Trignis handles token caching and automatic refresh, in either case no manual intervention is needed unless you've reached the token expiry time window.

### Message Queues

Trignis supports exporting database changes to message queues for asynchronous processing, event-driven architectures, and decoupled microservices.

**Supported Platforms:**

As of right now, we support three major platforms:

- **RabbitMQ** (`RabbitMQ`): Direct queues or exchange-based routing
- **Azure Service Bus** (`AzureServiceBus`): Queues and topics  
- **AWS SQS** (`AWSSQS`): Standard queues with IAM or explicit credentials

#### Quick Example (RabbitMQ)

```json
{
  "ChangeTracking": {
    "ApiEndpoints": [
      // Direct queue publish
      {
        "Key": "rabbitmq_direct_queue",
        "MessageQueueType": "RabbitMQ",
        "MessageQueue": {
          "HostName": "localhost",
          "Port": 5672,
          "VirtualHost": "/",
          "Username": "guest",
          "Password": "guest",
          "QueueName": "trignis-changes"
        }
      },
      // Exchange-based routing
      {
        "Key": "rabbitmq_exchange",
        "MessageQueueType": "RabbitMQ",
        "MessageQueue": {
          "HostName": "rabbitmq.example.com",
          "Port": 5672,
          "Username": "guest",
          "Password": "guest",
          "Exchange": "data-changes",
          "RoutingKey": "database.trainingsessions"
        }
      }
      }
    ]
  }
}
```

**Key Features:**

We've baked in a variety of features:

- Automatic message compression for payloads > 1KB
- Circuit breaker protection (opens after 3 failures, 1-minute recovery)
- Connection pooling and automatic reconnection (RabbitMQ)
- Dead letter queue for failed messages
- Health check endpoints for monitoring

> [!TIP]
> For complete documentation, configuration examples, and troubleshooting guides, see our [Wiki: About Message Queues](https://github.com/melosso/trignis/wiki/Guide:-Message-Queues).

### Custom Headers & Compression

Add correlation tracking and reduce bandwidth usage:

```json
{
  "Key": "compressed_webhook",
  "Url": "https://api.example.com/data/changes",
  "Auth": {
    "Type": "Bearer",
    "Token": "your-token"
  },
  "CustomHeaders": {
    "X-Correlation-Id": "{guid}",
    "X-Source": "trignis-{database}",
    "X-Timestamp": "{timestamp}",
    "X-Environment": "{environment}"
  },
  "EnableCompression": true
}
```

The configuration allows usage of variable substitution in headers:

- `{guid}`: Generates unique correlation ID
- `{key}`: Identifier of the tracking object
- `{timestamp}`: Current timestamp
- `{object}`: Tracking object name
- `{database}`: Database name
- `{environment}`: Environment name

Compression uses gzip encoding and can significantly reduce payload sizes for large change sets, though the recipient must support gzip decompression (which most modern servers do, but is not guaranteed).

### Large Payload Batching

When `InitialSyncMode: "Full"` returns thousands of records, Trignis automatically splits them into batches:

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "MaxRecordsPerBatch": 1000,
      "EnablePayloadBatching": true
    }
  }
}
```

Each batch includes headers for tracking:
- `X-Batch-Number`: Current batch (1, 2, 3...)
- `X-Total-Batches`: Total number of batches

Your API should buffer batches and process the complete dataset when all batches are received.

## ⚡ Risks

Using change tracking may slow database writes (for the tables that have this feature enabled) by approximately 5-10% because it tracks every single change. The tracking tables keep growing and need regular cleanup (see: [Compatibility](#-compatibility)) 

The dead letter mechanism does not automatically resync or retry failed messages when the application starts up/is running. The dead letters are stored in `sinkhole.db` as a sinkhole - they're saved for auditing/debugging purposes **but not automatically retried.**

> [!TIP]
> You can read more information about the performance risks (and relevant query's) of change tracking at this Brent Ozar article: [Performance Tuning SQL Server Change Tracking](https://www.brentozar.com/archive/2014/06/performance-tuning-sql-server-change-tracking/).

## 🔄 Compatibility

Compatibility with all SQL Server versions is **not** guaranteed. This application may not work at all on deprecated versions of Microsoft SQL Server.

Note that SQL Server 2025 introduced changes to change tracking autocleanup. In SQL Server 2025 (17.x) Preview and later versions, the autocleanup process introduces an adaptive shallow cleanup approach for large side tables, which is enabled by default. 

This differs from the deep cleanup in earlier versions. Note that the `sp_flush_CT_internal_table_on_demand [@TableToClean =] 'tablename'` procedure must be handled manually outside of Trignis.

For more details, refer to [About Change Tracking (SQL Server)](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-tracking-sql-server?view=sql-server-ver17) and [KB3173157](https://support.microsoft.com/en-us/topic/kb3173157-adds-a-stored-procedure-for-the-manual-cleanup-of-the-change-tracking-side-table-in-sql-server-2fe76677-8687-acc0-12a9-78f3709fc621).

> [!CAUTION]
> Before installing Trignis (or any change tracking application that leverages the same mechanism), make sure that you've read and understood these **cleanup** procedures. Failing to do so may result in data loss, degraded performance or incorrect change tracking behavior.

## 📊 Logging & Monitoring

We've implemented both logging and a separate health mechanism.

#### Logging

Trignis provides comprehensive logging with environment prefixes:

* Application logs: `log/trignis-.log` (daily rotation)
* Configuration status on startup
* Environment-specific processing: `[prod]`, `[dev]`, `[staging]`
* Change processing details
* Error handling with retry logic

> [!TIP]
> If the application isn't starting, consider changing value `UseEventLog` in the `appsettings.json` configuration file to true. To prevent your Windows Event Viewer from bloating, make sure to disable when you're done troubleshooting.

#### Monitoring

Trignis includes health check endpoints for monitoring service availability, database connectivity, and state tracking:

```json
{
  "Health": {
    "Enabled": true,
    "Port": 2455,
    "Host": "*",
    "CacheDurationSeconds": 120
  }
}
```

**Available Endpoints:**

```bash
# Service health and database connectivity
GET http://localhost:2455/health

# Dead letter statistics
GET http://localhost:2455/health/deadletters

# Message queue connection health
GET http://localhost:2455/health/connections

# State tracking per environment
GET http://localhost:2455/health/state
GET http://localhost:2455/health/state/production
```

**Example Response:**
```json
{
  "status": "healthy",
  "service": "trignis-service",
  "uptime": "43200s",
  "timestamp": "2025-10-21T13:08:00Z",
  "version": "2025.12.2",
  "checks": {
    "database": {
      "status": "ok (all)",
      "response_time_ms": 12
    }
  }
}
```

The state endpoint shows tracking versions per environment:

```json
{
  "timestamp": "2025-01-15T10:30:00Z",
  "total_environments": 2,
  "environments": [
    {
      "name": "production",
      "object_count": 3,
      "objects": [
        {
          "object_name": "Orders",
          "last_version": 1543,
          "last_updated": "2025-01-15T10:29:45Z"
        }
      ]
    }
  ]
}
```

## 🤝 Credits

Built with:

* [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
* [Dapper](https://github.com/DapperLib/Dapper)
* [Serilog](https://serilog.net/)
* [SQLite](https://www.sqlite.org/)
* [Polly](https://github.com/App-vNext/Polly)
* [RabbitMQ.Client](https://github.com/rabbitmq/rabbitmq-dotnet-client)
* [Azure.Messaging.ServiceBus](https://github.com/Azure/azure-sdk-for-net)
* [AWSSDK.SQS](https://github.com/aws/aws-sdk-net)

## 🔮 Lore

For those curious:

> Trignis blends the words Trigger and Ignis — Latin for fire or spark. In the world of databases, a trigger initiates automated actions in response to specific events. Combined with Ignis, it symbolizes the spark that sets automation in motion. Trignis meddles in that precise moment when automation should ignite.

## License

Free for open source projects and personal use under the **AGPL 3.0** license. For more information, please see the [license](LICENSE) file.

## Contributing

Contributions welcome! Please submit issues and pull requests, using the templates we provided.