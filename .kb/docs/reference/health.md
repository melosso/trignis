---
title: Health endpoints
description: JSON status routes
---

# Health endpoints

Trignis exposes a handful of plain JSON routes so your existing monitoring can see how it is doing without anyone opening the dashboard. You turn them on with `Health:Enabled`, and they are served on `Health:Port`, which defaults to 2455.

These routes are deliberately unauthenticated, since most monitoring agents are happier without a credential to manage. The information is available to any client that can reach the port. On a shared host: bind to localhost or restrict the port with a firewall.

## GET /health

The overall picture: is the service up, and can it still reach its databases?

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
    },
    "destinations": {
      "status": "ok",
      "unhealthy": [],
      "open_circuits": []
    }
  }
}
```

The top-level `status` is `healthy` if every database answers and no destination is failing. Otherwise it is `degraded`. `database.status` is `ok (all)`, `degraded (2/3)` or `failed (all)`.

`destinations.unhealthy` contains queues that failed their last probe and HTTP endpoints with a failed last export. `destinations.open_circuits` contains queues with an open circuit breaker.

Each call opens a connection to every unique database with a 5 second timeout. The response is cached for `Health:CacheDurationSeconds` (120 by default). Frequent polling does not add database load.

## GET /health/connections

Where `/health` asks the databases directly, this route reports what the background prober last observed. It answers instantly as a result, and is the better choice when you want a broad view including your message brokers.

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

Keys follow one of two shapes: `sql:{environment}/{connectionKey}` for databases, and `{queueType}:{endpointKey}` for queues. The first results appear about ten seconds after startup and refresh every `HealthCheckIntervalMinutes`.

RabbitMQ, Azure Service Bus, AWS SQS, Azure Event Hubs and Kafka are probed. A Kafka topic must exist to pass.

## GET /health/state

How far along each tracked object is. This is the route to reach for when you want to know whether data is actually moving, rather than whether connections are merely possible.

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

This reads directly from `state.db`, which means it lists objects that have stored state rather than objects that are configured. An object you have just added will be missing here until its first cycle completes, and that absence is normal rather than a fault.

Comparing `last_updated` against your polling interval is a good way to spot an object that has quietly stopped advancing, whether because a destination is failing or because it has been [paused](/guide/dashboard#pausing-change-tracking).

## GET /health/state/{environmentName}

The same information narrowed to a single environment, which keeps dashboards tidy when you run several. A 404 here means no state is stored under that name yet, rather than that the environment does not exist.

```json
{
  "environment": "production",
  "timestamp": "2026-07-28T10:00:00Z",
  "object_count": 2,
  "objects": []
}
```

## GET /health/deadletters

A count of what has failed to export recently.

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

`MostCommonError` looks at the last 24 hours, which usually points straight at whatever is currently broken. On a fresh install `sinkhole.db` does not exist yet, and you will get zeroes rather than an error.

## GET /

With the dashboard turned off, the root path serves a small JSON index of the routes above, which makes discovery easier for anyone poking at the service for the first time. When `WebHost:Enabled` is on, it redirects to `/ui` instead.

## Putting these to work

If you only wire up one route, make it `/health`. A non-200 means the process is not answering at all, and within a 200 the `status` field distinguishes `healthy` from `degraded`.

It is worth adding `/health/deadletters` as a second signal, though. A rising `LastHourCount` shows exports are failing right now.
