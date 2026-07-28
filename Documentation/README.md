# Documentation

Served by [Bark](https://github.com/melosso/bark). As probably familiar, the pages are plain markdown under `docs/`, with site configuration in `docs/config.json`.

## Running locally

```bash
docker compose up -d
```

The site becomes available at `http://localhost:5993`.

## Layout

| Path | Purpose |
|---|---|
| `docs/config.json` | Navigation, sidebar, branding |
| `docs/guide/` | Task-oriented pages: install, track a table, export, operate |
| `docs/reference/` | Settings: the stored procedure contract, endpoints |

Adding a page means dropping a `.md` file in and adding one sidebar entry to `config.json`.
