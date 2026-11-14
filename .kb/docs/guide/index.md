---
title: What is Trignis?
description: Change tracking for SQL Server and PostgreSQL, exported to files, APIs and queues
---

# What is Trignis?

Trignis is a change capture utility for SQL Server and PostgreSQL. After pointing it to a database, selecting target objects, and defining a destination, it continuously pushes updates downstream.

<div class="tip custom-block">

The documentation assumes that you have knowledge of database administration and the trade-offs that change data capture brings to the table.

</div>

It handles data integration tasks, for example (but not limited to) syncing replica databases, feeding external services, and recording audit histories.

## How it works

1. A **procedure you write** reads whatever tracks changes in your database and returns them as JSON.
2. Trignis calls it on a timer, once per tracked object.
3. Whatever comes back is exported to every destination configured for that environment.
4. The last processed version is stored, so the next poll only asks for what came after it.

The procedure is the important part of that list. Trignis does not generate SQL against your tables. You decide which columns leave the database and in what shape. See the [stored procedure contract](/reference/stored-procedure).

That is also why the database matters less than it looks. Trignis never calls `CHANGETABLE` itself, so the mechanism is your choice: SQL Server change tracking, a PostgreSQL outbox table, or an existing version column on a table with no change tracking at all. Step 1 is the only part that differs per platform.

## Supported databases

| Database | Provider | Usual mechanism |
|---|---|---|
| Microsoft SQL Server | `mssql` (default) | Change tracking, or an existing `rowversion` |
| PostgreSQL 13+ | `postgres` | Outbox table with a trigger, or a sequence column |

## Why polling, not triggers

Trignis polls on an interval rather than firing on every write.

Legacy applications often touch the same row many times before they are finished with it. A real-time hook would forward every intermediate state, including the forty-nine nobody wants. Polling collapses those into a single export of the settled row.

The trade is latency: changes arrive within one polling interval (30 seconds by default) rather than instantly. If you need per-write delivery, a trigger or an SDK is the better tool.

## What it is not

- **Not a replication engine.** There is no conflict resolution and no two-way sync.
- **Not a queue.** Trignis sends and moves on. Failed sends land in a [dead letter store](/guide/dead-letters) for replay.
- **Not real-time.** See above.

## Next

- [Install](/guide/install): get it running.
- [Track your first table](/guide/first-table): end to end, one table.
