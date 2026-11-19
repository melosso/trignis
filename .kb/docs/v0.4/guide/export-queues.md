---
title: Export to message queues
description: RabbitMQ, Azure Service Bus, AWS SQS, Event Hubs and Kafka
---

# Export to message queues

If your downstream systems already speak a queue, you can send changes there instead of (or alongside) an HTTP endpoint. The nice part is that there is nothing new to learn structurally: a queue endpoint is just another entry in the same `ApiEndpoints` list you already use, with `MessageQueueType` set where you would otherwise put a `Url`.

Five brokers are supported today, and you can mix them freely within one environment:

| `MessageQueueType` | Target |
|---|---|
| `RabbitMQ` | Direct queue or exchange routing |
| `AzureServiceBus` | Queue or topic |
| `AWSSQS` | Standard queues, IAM or explicit credentials |
| `AzureEventHubs` | Event streaming |
| `Kafka` | Topics, SASL/SSL or plaintext |

## RabbitMQ

The simplest arrangement publishes straight to a named queue:

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

If your topology is built around exchanges instead, you can route through one of those:

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

It is best to set one or the other. Should both end up present, the exchange takes precedence and Trignis will point this out in the startup log rather than leave you guessing.

Connections are pooled per host, port and virtual host, then reused across cycles, so a short polling interval does not mean a new connection every time.

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

Publishing to a topic works the same way: swap `QueueName` for `TopicName`. If both happen to be set, `QueueName` is the one that gets used.

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

Leaving out `AccessKeyId` and `SecretAccessKey` entirely is often the better choice, since Trignis then falls back to the default AWS credential chain and picks up instance roles, environment variables or a named profile. That keeps long-lived keys out of your configuration.

Whichever route you take, please supply both credentials or neither. Just one on its own is treated as a configuration error, because it almost always means something was half-edited.

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

For a plaintext broker you can simply omit `Username` and `Password`. When you do authenticate, `SaslMechanism` accepts `PLAIN`, `SCRAM-SHA-256` and `SCRAM-SHA-512`.

Producers are cached per broker and topic. Both Confluent Cloud and self-hosted brokers work without any special handling.

## Size limits

Every broker has its own idea of how large a message may be, and Trignis checks against that ceiling before attempting a send rather than letting the broker reject it:

| Platform | Limit |
|---|---|
| RabbitMQ | 128 MB |
| Azure Service Bus | 256 KB |
| AWS SQS | 256 KB |
| Azure Event Hubs | 1 MB |
| Kafka | 1 MB (broker default) |

Service Bus and SQS have the tightest limits of the group, so an oversized message destined for either is gzipped and base64-encoded first, with `Compressed` set in the message properties for your consumer to check. If it is still too large after that, the export becomes a [dead letter](/guide/dead-letters) rather than being silently dropped.

::: tip
Worth knowing: queue exports are not batched the way HTTP exports are. If you expect a full sync to exceed the limit, an HTTP endpoint handles that comfortably, and narrowing what your procedure returns is usually an improvement in its own right.
:::

## Circuit breaker

When a broker goes away, waiting on connection timeouts every cycle slows everything down for no benefit. So after three consecutive failures an endpoint opens its circuit for a minute and fails fast instead, closing again on the first success.

Each endpoint carries its own breaker. One unreachable queue therefore holds up only itself, and your other destinations carry on as normal.
