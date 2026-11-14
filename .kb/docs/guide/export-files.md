---
title: Export to files
description: Write changes to disk as JSON
---

# Export to files

The simplest destination, and the easiest to debug. You can open the output.

```json
{
  "ChangeTracking": {
    "ExportToFile": true,
    "FilePath": "exports/{environment}/{object}/{database}/changes-{timestamp}.json"
  }
}
```

Each export cycle that finds changes writes one file.

## Placeholders

| Placeholder | Replaced with |
|---|---|
| `{timestamp}` | UTC time, `yyyyMMddHHmmss` |
| `{object}` | Tracking object name |
| `{database}` | Connection string key |
| `{environment}` | Environment name |

Directories are created as needed.

## Keeping the directory in check

`FilePathSizeLimit` (MB, default 500) caps the export directory. Once over, oldest files are deleted until it fits.

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "FilePathSizeLimit": 500
    }
  }
}
```

The limit applies to the fixed part of your `FilePath`: the text before the first placeholder. With `exports/{environment}/...` that is `exports/`.

::: warning
If `FilePath` has no directory before the first placeholder (`"{object}-{timestamp}.json"`), cleanup is skipped entirely rather than sweeping the working directory. Keep a directory prefix if you want the limit enforced.
:::

## Combining destinations

File export is independent of the others. Both of these run in the same cycle:

```json
{
  "ChangeTracking": {
    "ExportToFile": true,
    "ExportToApi": true
  }
}
```

Each destination is attempted separately. One failing does not stop the others. The failure becomes a [dead letter](/guide/dead-letters) on its own.
