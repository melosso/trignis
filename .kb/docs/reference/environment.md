---
title: Environment files
description: Databases, tables and destinations
---

# Environment files

Each `.yaml` file in `environments/` describes one environment: the databases, the tables to track and the destinations for their changes. The file name without `.yaml` is the environment name: `production.yaml` defines the environment `production`. Sync state, pauses and dead letters are stored per environment name. Renaming the file resets them.

Trignis watches the folder. Saving a file reloads that environment within about a second. Files with another extension are ignored; a `.yml` file stops startup with a message to rename it.

```yaml
provider: mssql

databases:
  primary: "Server=sql.example.com;Database=PrimaryDB;Trusted_Connection=True;"

polling:
  interval: 30s

retry:
  count: 3
  delay: 5s

tables:
  - name: Items
    database: primary
    table: dbo.Items
    procedure: web.get_itemssync
    initial_sync: incremental

destinations:
  - name: archive
    type: file
    path: "exports/{environment}/{object}/changes-{timestamp}.json"
  - name: webhook
    type: http
    url: https://api.example.com/changes
```

Comments start with `#`. A ` #` after an unquoted value ends the value: put connection strings and secrets that contain `#` in double quotes. Trignis warns about an unquoted secret followed by a comment.

## provider

The database platform of every connection string in the file. Defaults to `mssql`.

| Value | Platform | Aliases |
|---|---|---|
| `mssql` | Microsoft SQL Server | `sqlserver` |
| `postgres` | PostgreSQL | `postgresql`, `pgsql` |

One environment is one platform. To track both, write two environment files. The provider sets how the procedure is called; see the [stored procedure contract](/reference/stored-procedure).

## databases

Database name to connection string, in the syntax of the provider. `tables[].database` refers to a name here. Values are encrypted in place on first read.

Trignis adds these keys unless the connection string sets them:

| Provider | Added unless present |
|---|---|
| `mssql` | `Application Name=Trignis`, `Packet Size=32768`, `Connect Timeout=30` |
| `postgres` | `Application Name=Trignis`, `Timeout=30` |

## polling and retry

| Key | Type | Purpose |
|---|---|---|
| `polling.interval` | duration | Time between cycles |
| `retry.count` | int | Attempts before a dead letter |
| `retry.delay` | duration | Time between attempts |

A duration is a number of seconds (`30`) or a number with `s`, `m` or `h` (`30s`, `5m`, `1h`). Omitted values use the [global settings](/reference/global-settings).

## tables

One entry per tracked table and the procedure that reads its changes.

| Field | Required | Purpose |
|---|---|---|
| `name` | yes | Identifier in state, logs and placeholders |
| `database` | yes | Name from `databases` |
| `table` | yes | Source table, for logs and placeholders |
| `procedure` | yes | Procedure Trignis calls |
| `initial_sync` | no | `incremental` (default) or `full` |

`initial_sync` applies when no version is stored: the first run, or after a [reset](/guide/dashboard).

- **`incremental`** stores the current version and exports nothing. Only later changes are sent.
- **`full`** exports every row, then continues incrementally.

::: warning
`name` is the key of the stored state. A renamed table re-initialises according to `initial_sync`.
:::

## destinations

Every destination receives every change from the environment. To send different tables to different places, use separate environment files.

| Field | Required | Purpose |
|---|---|---|
| `name` | yes | Name in logs, dead letters and `{key}`; unique per file |
| `type` | yes | `file`, `http`, `rabbitmq`, `azureservicebus`, `awssqs`, `azureeventhubs` or `kafka` |
| `enabled` | no | `false` keeps the destination configured and sends nothing |

The other fields depend on `type`:

| Type | Fields (required in bold) |
|---|---|
| `file` | **`path`** (one enabled file destination per environment) |
| `http` | **`url`**, `auth`, `headers`, `compression` |
| `rabbitmq` | **`host`**, `port`, `virtual_host`, `username`, `password`, `queue`, `exchange`, `routing_key` |
| `azureservicebus` | **`connection_string`**, `queue`, `topic` |
| `awssqs` | **`queue_url`**, `region`, `access_key_id`, `secret_access_key` |
| `azureeventhubs` | **`connection_string`**, **`event_hub`** |
| `kafka` | **`bootstrap_servers`**, **`topic`**, `username`, `password`, `security_protocol`, `sasl_mechanism` |

`auth` is described in [Authentication](/reference/authentication). Passwords, keys, connection strings and `headers` values are encrypted in place on first read. See [files](/guide/export-files), [HTTP](/guide/export-http) and [queues](/guide/export-queues) for each type.

## Placeholders

Available in `path`, `url` and `headers`:

| Placeholder | Value |
|---|---|
| `{timestamp}` | UTC `yyyyMMddHHmmss` |
| `{object}` | Table name |
| `{database}` | Database name |
| `{environment}` | Environment name |
| `{key}` | Destination name, URLs only |
| `{guid}` | New GUID, headers only |
| `{batch}`, `{totalbatches}` | Batch position, headers only |

## Validation

Trignis validates every file at startup and on each save. Each problem is reported with its line and column.

- **Errors** stop startup: invalid YAML, a duplicate key, a value of the wrong type, a missing required field, a `database` that is not under `databases`, a duplicate table or destination name.
- **On save**, an error rejects the edit. The previous version keeps running, and the dashboard shows a warning badge.
- **Warnings** do not stop anything: an unknown key, with a suggestion when the key is close to a valid one.

## Migrating from JSON

Trignis v0.5 converts each `environments/*.json` file to `.yaml` at startup and on save, and logs `Migrating environments to new format`. The JSON file is kept as `name.json.migrated`, with its secrets encrypted. The global `ExportToFile`, `ExportToApi` and `FilePath` settings are written into the YAML file as destinations; a disabled export gets `enabled: false`. If `name.yaml` already exists, the JSON file is left unchanged and Trignis logs a warning.

- **Read-only folder** (a ConfigMap or a `:ro` mount): Trignis cannot write the YAML file. It logs a warning and loads the JSON file in memory. Convert the file before v0.6.
- **Conversion error:** a JSON file that cannot be converted, or that does not load back to the same environment, stops startup. On save, the edit is rejected.

v0.6 removes the migration.
