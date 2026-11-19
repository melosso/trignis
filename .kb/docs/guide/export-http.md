---
title: Export to HTTP endpoints
description: POST changes to an API, with auth, headers and batching
---

# Export to HTTP endpoints

An HTTP destination has `type: http`. Every destination receives every change for that environment.

```yaml
destinations:
  - name: PowerAutomate
    type: http
    url: https://your-webhook-url
    auth:
      type: bearer
      token: your-token-here
```

The payload is the `Data` array from your stored procedure, POSTed as JSON.

`name` identifies the destination in logs and dead letters.

## Authentication

`bearer`, `basic`, `apikey` and `oauth2` are supported. See [Authentication](/reference/authentication) for each one's fields.

Credentials are encrypted in place the first time Trignis reads the file.

## Custom headers

```yaml
destinations:
  - name: webhook
    type: http
    url: https://api.example.com/changes
    compression: true
    headers:
      X-Correlation-Id: "{guid}"
      X-Source: "trignis-{database}"
      X-Environment: "{environment}"
```

Header values are encrypted in place on first read.

Placeholders: `{guid}`, `{key}`, `{timestamp}`, `{object}`, `{database}`, `{environment}`, plus `{batch}` and `{totalbatches}` when batching.

The URL accepts the same placeholders, URL-encoded.

`compression: true` gzips the body and sets `Content-Encoding: gzip`. Confirm the receiver handles it. Most servers do, but not all.

## Batching

A full sync of a large table produces one enormous request. Batching splits it:

In `appsettings.json`:

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

Failed requests retry per `retry.count` and `retry.delay`. Retries cover transport failures and non-success status codes. After the last attempt the payload becomes a [dead letter](/guide/dead-letters).
