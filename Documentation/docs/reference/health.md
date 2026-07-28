---
title: Health endpoints
description: JSON status routes
---

# Health endpoints

Trignis exposes a handful of plain JSON routes so your existing monitoring can see how it is doing without anyone opening the dashboard. You turn them on with `Health:Enabled`, and they are served on `Health:Port`, which defaults to 2455.

These routes are deliberately unauthenticated, since most monitoring agents are happier without a credential to manage. That does mean the information is available to anything that can reach the port, so binding to localhost or keeping the port behind a firewall is a sensible precaution when the host is shared.

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
    }
  }
}
```

The top-level `status` reads `healthy` only when every configured database answers, so it is a genuinely strict signal rather than a liveness ping. When something is off, `database.status` tells you how much: `ok (all)`, `degraded (2/3)` or `failed (all)`.

Behind that answer, each call opens a real connection to every unique database with a 5 second timeout. Because that is real work, the response is cached for `Health:CacheDurationSeconds` (120 by default), which lets you poll as often as you like without turning your health check into a source of load.

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

Only RabbitMQ, Azure Service Bus and AWS SQS are probed at the moment, so Event Hubs and Kafka endpoints will not show up here. If those matter to your alerting, watching `/health/deadletters` covers them indirectly: a broker that stops accepting messages shows up there quickly enough.

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

It is worth adding `/health/deadletters` as a second signal, though. A rising `LastHourCount` tells you exports are failing right now, and that is something `/health` genuinely cannot see: a database can be perfectly reachable while every downstream endpoint refuses what you send it. The two together cover both halves of the pipeline.
