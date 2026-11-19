---
title: Delivery contract
description: Guarantees and headers of exported payloads
---

# Delivery contract

The delivery contract defines the guarantees and headers of every payload Trignis sends to HTTP endpoints and message queues.

## Guarantees

| Guarantee | Rule |
|---|---|
| At least once | A payload can arrive more than once. Repeats occur on a retry, on a dead letter replay, and on a stop between the export and the version update. A payload that fails every retry is stored as a [dead letter](/guide/dead-letters). |
| Order per table | Payloads of one table are sent in version order. Payloads of different tables have no defined order. A replayed dead letter is sent after newer payloads of the same table. |
| Row version | Each row in `Data` has the `$version` assigned by the procedure. `$version` and the row key identify one change. |

Receivers upsert by key and discard a row whose `$version` is lower than the stored version.

## Payload

The request body is the `Data` array returned by the [procedure](/reference/stored-procedure).

If `EnablePayloadBatching` is `true` and the array exceeds `MaxRecordsPerBatch` rows: Trignis splits the array into batches of `MaxRecordsPerBatch` rows and sends one request per batch.

## HTTP headers

| Header | Value | Sent |
|---|---|---|
| `X-Trignis-Delivery` | `<payload hash>-<batch number>` | Every request |
| `X-Trignis-Signature` | `t=<unix seconds>,v1=<hex HMAC-SHA256>` | If the destination has `signing_secret` |
| `X-Batch-Number`, `X-Total-Batches` | Batch number, batch count | If the payload is split into batches |

`X-Trignis-Delivery` is identical for every retry and replay of the same payload. The payload hash equals the `DataHash` column of the dead letter. A different `MaxRecordsPerBatch` produces different batches and different delivery ids.

Receivers deduplicate by storing processed delivery ids and discarding requests with a stored id.

## Signature

`signing_secret` on an `http` destination enables `X-Trignis-Signature`:

```yaml
destinations:
  - name: erp
    type: http
    url: "https://erp.example.com/hooks/trignis"
    signing_secret: "a-long-random-value"
```

The signature is the HMAC-SHA256 of `<t>.<body>`, keyed with `signing_secret`.

- `<t>` is the timestamp from the header.
- `<body>` is the uncompressed JSON body. If `compression` is `true`: decompress the body before verification.

Receivers reject a request if the signature does not match or if `t` is more than 300 seconds old.

Verification requires the raw body. Parsing and re-serializing the JSON changes whitespace or key order and invalidates the signature.

::: code-group
```csharp [C# icon:dotnet]
static bool Verify(string header, string body, string secret)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var t = long.Parse(parts["t"]);
    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300) return false;
    var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
    return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["v1"]));
}
```
```js [Node.js icon:nodedotjs]
const crypto = require("crypto");

function verify(header, body, secret) {
  const parts = Object.fromEntries(header.split(",").map(p => p.split("=", 2)));
  if (Math.abs(Date.now() / 1000 - Number(parts.t)) > 300) return false;
  const expected = crypto.createHmac("sha256", secret).update(`${parts.t}.${body}`).digest("hex");
  return crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1));
}
```
```python [Python icon:python]
import hashlib, hmac, time

def verify(header: str, body: str, secret: str) -> bool:
    parts = dict(p.split("=", 1) for p in header.split(","))
    if abs(time.time() - int(parts["t"])) > 300:
        return False
    expected = hmac.new(secret.encode(), f"{parts['t']}.{body}".encode(), hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, parts["v1"])
```
:::

## Queues

Queue messages contain the same payload as HTTP requests. Queue messages do not include the delivery id or the signature. Broker authentication identifies the sender.
