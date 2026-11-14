---
title: Environment files
description: Connection strings, tracked objects and endpoints
---

# Environment files

Each file in `environments/` describes one self-contained environment: where to connect, what to track there, and where the changes should go. The filename without its extension becomes the environment name, so `production.json` gives you an environment called `production`.

These files are watched. Saving one reloads that environment within about a second, which means you can iterate on what you track without restarting anything.

```json
{
  "Provider": "mssql",
  "ConnectionStrings": {
    "PrimaryDatabase": "Server=sql.example.com;Database=PrimaryDB;Trusted_Connection=True;"
  },
  "ChangeTracking": {
    "PollingIntervalSeconds": 30,
    "ExportToFile": true,
    "FilePath": "exports/{environment}/{object}/changes-{timestamp}.json",
    "ExportToApi": false,
    "RetryCount": 3,
    "RetryDelaySeconds": 5,
    "TrackingObjects": [],
    "ApiEndpoints": []
  }
}
```

## Provider

The database platform every connection string in this file points at. Optional, defaults to `mssql`.

| Value | Platform | Aliases |
|---|---|---|
| `mssql` | Microsoft SQL Server | `sqlserver` |
| `postgres` | PostgreSQL | `postgresql`, `pgsql` |

One environment is one platform. To track both, write two environment files.

The provider decides how the procedure is invoked and whether Trignis can read a watermark from the server. See the [stored procedure contract](/reference/stored-procedure).

## ConnectionStrings

A map of key to connection string, in the syntax of the chosen `Provider`. Keys are referenced by `TrackingObjects[].Database`.

Values are encrypted in place on first read.

Trignis fills in a few keys, but only when you have not set them yourself:

| Provider | Applied unless present |
|---|---|
| `mssql` | `Application Name=Trignis`, `Packet Size=32768`, `Connect Timeout=30` |
| `postgres` | `Application Name=Trignis`, `Timeout=30` |

## ChangeTracking

Everything in this section is optional. Anything you leave out falls back to your [global settings](/reference/global-settings), so it is quite reasonable for this block to contain nothing but `TrackingObjects` until you find a reason to differ.

| Key | Type | Purpose |
|---|---|---|
| `PollingIntervalSeconds` | int | Seconds between cycles for this environment |
| `ExportToFile` | bool | Write changes to disk |
| `FilePath` | string | Output template, supports placeholders |
| `ExportToApi` | bool | Send to `ApiEndpoints` |
| `RetryCount` | int | Attempts before a dead letter |
| `RetryDelaySeconds` | int | Delay between attempts |

## TrackingObjects

Each entry here is one table you would like watched, paired with the procedure that reads its changes:

```json
{
  "Name": "Items",
  "Database": "PrimaryDatabase",
  "TableName": "dbo.Items",
  "StoredProcedureName": "web.get_itemssync",
  "InitialSyncMode": "Incremental"
}
```

| Field | Required | Purpose |
|---|---|---|
| `Name` | yes | Identifier in state, logs and placeholders |
| `Database` | yes | Key from `ConnectionStrings` |
| `TableName` | yes | Source table, used for logging and placeholders |
| `StoredProcedureName` | yes | Procedure Trignis executes |
| `InitialSyncMode` | no | `Incremental` (default) or `Full` |

`InitialSyncMode` only comes into play when there is no stored version yet, which means the very first run or a [reset](/guide/dashboard) from the dashboard. You have two options:

- **`Incremental`** adopts the current version and exports nothing, so only changes made from that point onwards are sent. This is the default, on the grounds that pointing Trignis at a large table should not immediately push its entire contents downstream.
- **`Full`** exports every row first, then continues incrementally. Choose this when the downstream system is starting empty and needs the history.

::: warning
`Name` is what Trignis uses as the key for stored state. Renaming an object therefore reads as a brand new object, and it will re-initialise according to its `InitialSyncMode`. Worth keeping in mind before a tidy-up rename on a `Full` object.
:::

## ApiEndpoints

Destinations for the changes Trignis reads. An HTTP endpoint looks like this:

```json
{
  "Key": "webhook",
  "Url": "https://api.example.com/changes",
  "Auth": { "Type": "Bearer", "Token": "..." },
  "CustomHeaders": { "X-Correlation-Id": "{guid}" },
  "EnableCompression": false
}
```

A queue endpoint lives in the same list and is told apart by the presence of `MessageQueueType`:

```json
{
  "Key": "rabbit",
  "MessageQueueType": "RabbitMQ",
  "MessageQueue": { "HostName": "localhost", "QueueName": "changes" }
}
```

| Field | Purpose |
|---|---|
| `Key` | Name used in logs, dead letters and `{key}` |
| `Url` | Target URL, HTTP endpoints only |
| `Auth` | See [Authentication](/reference/authentication) |
| `CustomHeaders` | Extra headers, placeholders supported |
| `EnableCompression` | gzip the request body |
| `MessageQueueType` | `RabbitMQ`, `AzureServiceBus`, `AWSSQS`, `AzureEventHubs`, `Kafka` |
| `MessageQueue` | Platform settings, see [queues](/guide/export-queues) |

It is worth knowing that every endpoint in an environment receives every change from that environment. There is no per-object routing within a single file.

When you need different objects going to different places, splitting them across separate environment files is the way to do it, and it has the pleasant side effect of letting each one have its own polling interval and retry settings.

## Placeholders

These are available in `FilePath`, `Url` and `CustomHeaders`, which makes it easy to keep exports organised without hardcoding a path per object:

| Placeholder | Value |
|---|---|
| `{timestamp}` | UTC `yyyyMMddHHmmss` |
| `{object}` | Tracking object name |
| `{database}` | Connection string key |
| `{environment}` | Environment name |
| `{key}` | Endpoint key, URLs only |
| `{guid}` | Fresh GUID, headers only |
| `{batch}`, `{totalbatches}` | Batch position, headers only |

## Validation

Configuration is validated at startup. Errors stop the service; warnings are logged and it continues. Common errors: a `Database` with no matching connection string, a missing `StoredProcedureName`, a queue endpoint with no `MessageQueue`, an unknown `MessageQueueType`.
