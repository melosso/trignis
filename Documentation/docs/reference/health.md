---
title: Health endpoints
description: JSON status routes
---

# Health endpoints

Enabled with `Health:Enabled`, served on `Health:Port` (default 2455). No authentication: bind to localhost or firewall the port if that matters.

## GET /health

Service status and database connectivity.

```json
{
  "status": "healthy",
  "service": "trignis-service",
  "uptime": "3600s",
  "timestamp": "2026-07-28T10:00:00Z",
  "version": "1.3.3",
  "checks": {
    "database": {
      "status": "ok (all)",
      "response_time_ms": 42
    }
  }
}
```

`status` is `healthy` only when every configured database answers. `database.status` is `ok (all)`, `degraded (2/3)` or `failed (all)`.

Each call opens a real connection per unique database with a 5 second timeout, so the response is cached for `Health:CacheDurationSeconds` (default 120).

## GET /health/connections

Last known state of every monitored connection, from the background prober rather than a live check.

```json
{
  "sql:production/PrimaryDatabase": {
    "is_healthy": true,
    "last_error": null
  },
  "RabbitMQ:rabbit_main": {
    "is_healthy": false,
    "last_error": "3 consecutive failure(s)"
  }
}
```

Keys are `sql:{environment}/{connectionKey}` or `{queueType}:{endpointKey}`. Populated 10 seconds after startup, refreshed every `HealthCheckIntervalMinutes`.

Only RabbitMQ, Azure Service Bus and AWS SQS are probed. Event Hubs and Kafka endpoints do not appear.

## GET /health/state

Sync position of every tracked object.

```json
{
  "timestamp": "2026-07-28T10:00:00Z",
  "total_environments": 1,
  "environments": [
    {
      "name": "production",
      "object_count": 2,
      "objects": [
        {
          "object_name": "Items",
          "stored_procedure_name": "web.get_itemssync",
          "last_version": 12345,
          "last_updated": "2026-07-28T09:59:30Z"
        }
      ]
    }
  ]
}
```

This reads `state.db`, so it lists objects with stored state. An object configured but not yet initialised does not appear.

## GET /health/state/{environmentName}

The same data for one environment. Returns 404 if no state is stored under that name.

```json
{
  "environment": "production",
  "timestamp": "2026-07-28T10:00:00Z",
  "object_count": 2,
  "objects": []
}
```

## GET /health/deadletters

Dead letter counts.

```json
{
  "TotalCount": 12,
  "LastHourCount": 0,
  "Last24HoursCount": 3,
  "Last7DaysCount": 12,
  "MostCommonError": "Connection timeout",
  "MostCommonErrorCount": 7
}
```

`MostCommonError` covers the last 24 hours. Zeroes are returned if `sinkhole.db` does not exist yet.

## GET /

Service discovery when the UI is disabled: a JSON index of the routes above. With `WebHost:Enabled`, it redirects to `/ui` instead.

## Monitoring

`/health` is the endpoint to poll. `status` is `healthy` or `degraded`; a non-200 means the process is not answering at all.

`/health/deadletters` is worth alerting on. A rising `LastHourCount` means exports are failing now, which `/health` alone will not tell you, since a database can be perfectly reachable while every downstream endpoint refuses.
