---
title: Export to files
description: Write changes to disk as JSON
---

# Export to files

The simplest destination, and the easiest to debug. You can open the output.

```yaml
destinations:
  - name: archive
    type: file
    path: "exports/{environment}/{object}/{database}/changes-{timestamp}.json"
```

One enabled file destination is supported per environment.

Each export cycle that finds changes writes one file.

## Placeholders

| Placeholder | Replaced with |
|---|---|
| `{timestamp}` | UTC time, `yyyyMMddHHmmss` |
| `{object}` | Table name |
| `{database}` | Database name |
| `{environment}` | Environment name |

If the file name exists: Trignis appends `-1`, `-2` before the extension. The application temporarily writes each file to `.trignis-tmp/` before moving it to the destination.

Directories are created as needed.

## Keeping the directory in check

`FilePathSizeLimit` in `appsettings.json` (MB, default 500) caps the export directory. If the directory exceeds the limit: Trignis deletes the oldest files until the directory is within the limit.

```json
{
  "ChangeTracking": {
    "GlobalSettings": {
      "FilePathSizeLimit": 500
    }
  }
}
```

The limit applies to the fixed part of `path`: the text before the first placeholder. With `exports/{environment}/...` that is `exports/`.

::: warning
If `path` has no directory before the first placeholder (`"{object}-{timestamp}.json"`), cleanup is skipped. Add a directory prefix to enforce the limit.
:::

## Combining destinations

File export is independent of the others. Both of these run in the same cycle:

```yaml
destinations:
  - name: archive
    type: file
    path: "exports/{object}/changes-{timestamp}.json"
  - name: webhook
    type: http
    url: https://api.example.com/changes
```

Each destination is attempted separately. One failing does not stop the others. The failure becomes a [dead letter](/guide/dead-letters) on its own.
