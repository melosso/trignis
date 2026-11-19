---
title: Export to HTTP endpoints
description: POST changes to an API, with auth, headers and batching
---

# Export to HTTP endpoints

Set `ExportToApi` and list the endpoints. Every endpoint receives every change for that environment.

```json
{
  "ChangeTracking": {
    "ExportToApi": true,
    "ApiEndpoints": [
      {
        "Key": "PowerAutomate",
        "Url": "https://your-webhook-url",
        "Auth": {
          "Type": "Bearer",
          "Token": "your-token-here"
        }
      }
    ]
  }
}
```

The payload is the `Data` array from your stored procedure, POSTed as JSON.

`Key` names the endpoint in logs and dead letters. Make it recognisable.

## Authentication

`Bearer`, `Basic`, `ApiKey` and `OAuth2ClientCredentials` are supported. See [Authentication](/reference/authentication) for each one's fields.

Credentials are encrypted in place the first time Trignis reads the file.

## Custom headers

```json
{
  "Key": "webhook",
  "Url": "https://api.example.com/changes",
  "CustomHeaders": {
    "X-Correlation-Id": "{guid}",
    "X-Source": "trignis-{database}",
    "X-Environment": "{environment}"
  },
  "EnableCompression": true
}
```

Placeholders: `{guid}`, `{key}`, `{timestamp}`, `{object}`, `{database}`, `{environment}`, plus `{batch}` and `{totalbatches}` when batching.

The URL accepts the same placeholders, URL-encoded.

`EnableCompression` gzips the body and sets `Content-Encoding: gzip`. Confirm the receiver handles it. Most servers do, but not all.

## Batching

A full sync of a large table produces one enormous request. Batching splits it:

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "EnablePayloadBatching": true,
      "MaxRecordsPerBatch": 1000
    }
  }
}
```

Each batch carries `X-Batch-Number` and `X-Total-Batches`. Batches are sent sequentially.

::: warning
Batches are separate HTTP requests with no transaction around them. If batch 3 of 5 fails, the first two are already delivered. Buffer on the receiving side and commit when the last batch arrives.
:::

## Size limit

`MaxPayloadSizeBytes` (default 5 MB) is checked against the bytes actually sent, after compression. Over the limit, the export fails and becomes a dead letter rather than being sent.

It is not retried, because a retry cannot make the body smaller. Lower `MaxRecordsPerBatch`, turn batching on, or raise the limit.

## Retries

Failed requests retry per `RetryCount` and `RetryDelaySeconds`. Retries cover transport failures and non-success status codes. After the last attempt the payload becomes a [dead letter](/guide/dead-letters).
