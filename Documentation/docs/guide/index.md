---
title: What is Trignis?
description: Change tracking for SQL Server, exported to files, APIs and queues
---

# What is Trignis?

Trignis watches your SQL Server tables and tells something else when they change. You point it at a database, say which objects to track and where changes should go, and it keeps sending them.

It exists for the plumbing between systems: keeping a downstream copy in step, feeding an integration, building an audit trail.

## How it works

1. A **stored procedure you write** reads SQL Server's change tracking and returns the changes as JSON.
2. Trignis calls it on a timer, once per tracked object.
3. Whatever comes back is exported to every destination configured for that environment.
4. The last processed version is stored, so the next poll only asks for what came after it.

The stored procedure is the important part of that list. Trignis does not generate SQL against your tables. You decide which columns leave the database and in what shape. See the [stored procedure contract](/reference/stored-procedure).

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
