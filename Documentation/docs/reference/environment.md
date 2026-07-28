---
title: Environment files
description: Connection strings, tracked objects and endpoints
---

# Environment files

One file per environment in `environments/`. The filename without its extension becomes the environment name. Files are watched, so saving one reloads that environment within a second.

```json
{
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

## ConnectionStrings

A map of key to SQL Server connection string. Keys are referenced by `TrackingObjects[].Database`.

Values are encrypted in place on first read. Trignis sets `Application Name=Trignis`, and unless the string already specifies `Packet Size`, raises it to 32768 with a 30 second connect timeout for large payloads.

## ChangeTracking

Every key here is optional. Omitted, it falls back to [global settings](/reference/global-settings).

| Key | Type | Purpose |
|---|---|---|
| `PollingIntervalSeconds` | int | Seconds between cycles for this environment |
| `ExportToFile` | bool | Write changes to disk |
| `FilePath` | string | Output template, supports placeholders |
| `ExportToApi` | bool | Send to `ApiEndpoints` |
| `RetryCount` | int | Attempts before a dead letter |
| `RetryDelaySeconds` | int | Delay between attempts |

## TrackingObjects

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

`InitialSyncMode` only applies when no version is stored yet, which means first run or after a [reset](/guide/dashboard):

- **`Incremental`** adopts the current version and exports nothing. Only later changes are sent.
- **`Full`** exports every row, then continues incrementally.

::: warning
`Name` is the state key. Renaming an object makes Trignis treat it as new and re-initialise it.
:::

## ApiEndpoints

An HTTP endpoint:

```json
{
  "Key": "webhook",
  "Url": "https://api.example.com/changes",
  "Auth": { "Type": "Bearer", "Token": "..." },
  "CustomHeaders": { "X-Correlation-Id": "{guid}" },
  "EnableCompression": false
}
```

A queue endpoint, distinguished by `MessageQueueType`:

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

Every endpoint receives every change in the environment. There is no per-object routing; separate environments if you need that.

## Placeholders

Valid in `FilePath`, `Url` and `CustomHeaders`:

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
