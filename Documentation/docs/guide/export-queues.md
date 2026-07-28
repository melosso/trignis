---
title: Export to message queues
description: RabbitMQ, Azure Service Bus, AWS SQS, Event Hubs and Kafka
---

# Export to message queues

A queue endpoint is an entry in the same `ApiEndpoints` list, with `MessageQueueType` set instead of `Url`.

| `MessageQueueType` | Target |
|---|---|
| `RabbitMQ` | Direct queue or exchange routing |
| `AzureServiceBus` | Queue or topic |
| `AWSSQS` | Standard queues, IAM or explicit credentials |
| `AzureEventHubs` | Event streaming |
| `Kafka` | Topics, SASL/SSL or plaintext |

## RabbitMQ

Publish straight to a queue:

```json
{
  "Key": "rabbitmq_direct",
  "MessageQueueType": "RabbitMQ",
  "MessageQueue": {
    "HostName": "localhost",
    "Port": 5672,
    "VirtualHost": "/",
    "Username": "guest",
    "Password": "guest",
    "QueueName": "trignis-changes"
  }
}
```

Or route through an exchange:

```json
{
  "Key": "rabbitmq_exchange",
  "MessageQueueType": "RabbitMQ",
  "MessageQueue": {
    "HostName": "rabbitmq.example.com",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest",
    "Exchange": "data-changes",
    "RoutingKey": "database.items"
  }
}
```

Set one or the other. If both are present the exchange wins and Trignis warns at startup.

Connections are pooled per host/port/vhost and reused across cycles.

## Azure Service Bus

```json
{
  "Key": "servicebus",
  "MessageQueueType": "AzureServiceBus",
  "MessageQueue": {
    "ConnectionString": "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...",
    "QueueName": "trignis-changes"
  }
}
```

Use `TopicName` instead for topics. If both are set, `QueueName` wins.

## AWS SQS

```json
{
  "Key": "sqs",
  "MessageQueueType": "AWSSQS",
  "MessageQueue": {
    "QueueUrl": "https://sqs.eu-west-1.amazonaws.com/123456789/changes",
    "Region": "eu-west-1",
    "AccessKeyId": "AKIA...",
    "SecretAccessKey": "..."
  }
}
```

Omit both credentials to use the default AWS credential chain: instance roles, environment, profile. Supply both or neither; one alone is a configuration error.

## Azure Event Hubs

```json
{
  "Key": "eventhubs",
  "MessageQueueType": "AzureEventHubs",
  "MessageQueue": {
    "ConnectionString": "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...",
    "EventHubName": "trignis-changes"
  }
}
```

## Kafka

```json
{
  "Key": "kafka",
  "MessageQueueType": "Kafka",
  "MessageQueue": {
    "BootstrapServers": "broker1:9092,broker2:9092",
    "Topic": "trignis-changes",
    "Username": "user",
    "Password": "secret",
    "SecurityProtocol": "SASL_SSL",
    "SaslMechanism": "SCRAM-SHA-256"
  }
}
```

Omit `Username`/`Password` for plaintext. `SaslMechanism` accepts `PLAIN`, `SCRAM-SHA-256`, `SCRAM-SHA-512`. Producers are cached per broker/topic. Works with Confluent Cloud and self-hosted brokers.

## Size limits

Each platform has its own ceiling, enforced before sending:

| Platform | Limit |
|---|---|
| RabbitMQ | 128 MB |
| Azure Service Bus | 256 KB |
| AWS SQS | 256 KB |
| Azure Event Hubs | 1 MB |
| Kafka | 1 MB (broker default) |

For Service Bus and SQS, an oversized message is gzipped and base64-encoded first, with `Compressed` set in the message properties. Still too large after that and the export fails to a dead letter.

::: tip
Note that queue exports are not batched the way HTTP exports are. If a full sync will exceed the limit, use an HTTP endpoint or narrow what the stored procedure returns.
:::

## Circuit breaker

After 3 consecutive failures an endpoint opens its circuit for one minute, and calls fail immediately instead of waiting on timeouts. It closes again on the next success. Each endpoint has its own breaker, so one broken queue does not stall the others.
