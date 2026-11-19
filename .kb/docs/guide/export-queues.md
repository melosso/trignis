---
title: Export to message queues
description: RabbitMQ, Azure Service Bus, AWS SQS, Event Hubs and Kafka
---

# Export to message queues

A queue is a destination with a queue `type`. Queue, HTTP and file destinations can be combined in one environment.

| `type` | Target |
|---|---|
| `rabbitmq` | Queue or exchange |
| `azureservicebus` | Queue or topic |
| `awssqs` | Standard queue, IAM role or explicit credentials |
| `azureeventhubs` | Event hub |
| `kafka` | Topic, SASL/SSL or plaintext |

## RabbitMQ

Publish to a queue:

```yaml
destinations:
  - name: rabbitmq_direct
    type: rabbitmq
    host: localhost
    port: 5672
    username: guest
    password: guest
    queue: trignis-changes
```

Publish to an exchange with a routing key:

```yaml
destinations:
  - name: rabbitmq_exchange
    type: rabbitmq
    host: rabbitmq.example.com
    username: guest
    password: guest
    exchange: data-changes
    routing_key: database.items
```

Set `queue` or `exchange`. If both are set, `exchange` is used and the startup log reports it. `port` defaults to `5672` and `virtual_host` to `/`.

Connections are cached per host, port, virtual host and username, and reused across cycles.

## Azure Service Bus

```yaml
destinations:
  - name: servicebus
    type: azureservicebus
    connection_string: "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=..."
    queue: trignis-changes
```

For a topic, set `topic` instead of `queue`. If both are set, `queue` is used.

## AWS SQS

```yaml
destinations:
  - name: sqs
    type: awssqs
    queue_url: https://sqs.eu-west-1.amazonaws.com/123456789/changes
    region: eu-west-1
    access_key_id: AKIA...
    secret_access_key: "..."
```

Without `access_key_id` and `secret_access_key`, Trignis uses the default AWS credential chain: instance roles, environment variables or a named profile. Set both keys or neither; one key alone is a configuration error.

## Azure Event Hubs

```yaml
destinations:
  - name: eventhubs
    type: azureeventhubs
    connection_string: "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=..."
    event_hub: trignis-changes
```

## Kafka

```yaml
destinations:
  - name: kafka
    type: kafka
    bootstrap_servers: broker1:9092,broker2:9092
    topic: trignis-changes
    username: user
    password: secret
    security_protocol: SASL_SSL
    sasl_mechanism: SCRAM-SHA-256
```

For a plaintext broker, omit `username` and `password`. `sasl_mechanism` accepts `PLAIN`, `SCRAM-SHA-256` and `SCRAM-SHA-512`. Producers are cached per broker, topic, username and security settings.

## Size limits

| Platform | Limit |
|---|---|
| RabbitMQ | 128 MB |
| Azure Service Bus | 256 KB |
| AWS SQS | 256 KB |
| Azure Event Hubs | 1 MB |
| Kafka | 1 MB (broker default) |

A payload above the limit is split in halves until each message fits. Service Bus and SQS messages above the limit are gzipped and base64-encoded first, with `Compressed` set in the message properties. A single row that exceeds the limit becomes a [dead letter](/guide/dead-letters).

## Circuit breaker

After three consecutive failures, a destination fails fast for one minute, then retries. Each destination has its own circuit breaker: one unreachable broker does not delay the others.
