# Dashboard

## Purpose
A free-form, tile-based dashboard view that complements the Kanban board. Each tile displays content read from the project's `.dashboard/` folder. Users and agents can add, edit, remove, move, and resize tiles; layout is persisted per project so it survives restarts. Agents write files directly into tile folders; the dashboard discovers them automatically.

## Folder-per-tile layout

Each tile owns a dedicated subfolder under `.dashboard/`:

```
.dashboard/
  <tileSlug>/
    tile.yaml      # sidecar: template, title, refresh, prompt, model
    script.*       # optional script (.ps1, .sh, .js, .py) — convention-based, no field in sidecar
    output.*       # rendered content (extension determined by template)
```

The **tile slug** (`<tileSlug>`) is the folder name and the stable identifier used in all API calls.

### Sidecar fields (`tile.yaml`)

The sidecar YAML inside each tile folder contains five fields:
- `template` — required — which renderer to use (`markdown`, `table`, `kpi`, `kpi-grid`, `progress`, `sparkline`, `bar-chart`, `donut`, `gauge`, `status-grid`, `heatmap`, `leaderboard`, `timeline`, `image`, `mermaid`).
- `title` — optional display title shown in the tile header (falls back to the slug when absent).
- `refresh` — auto-refresh interval in seconds. `0` = static (no auto-refresh; can still be regenerated on demand by clicking the refresh button if a script or prompt is set).
- `prompt` — optional LLM instruction sent to `claude` to (re)generate the output file. Omit for script-only tiles.
- `model` — optional Claude model override (`""` = project default).

Scripts are **not** declared in `tile.yaml`. Presence of a `script.*` file in the tile folder is the sole convention; the interpreter is resolved from the extension (`.ps1` → pwsh/powershell, `.sh` → bash/Git Bash, `.js` → node, `.py` → python).

### Content pipeline

`DashboardRefreshService` polls every ~10 s for tiles whose sidecar declares `refresh > 0` and the interval has elapsed. When triggered it runs: **script first** (if `script.*` exists), then **prompt** (if set). Results are written to `output.*`. Manual refresh from the tile header works for any tile that has a script or a prompt, regardless of `refresh`.

The last refresh timestamp is persisted per tile in a `dashboard_tile_refresh_state` SQLite table (in the per-project DB). On startup, `DashboardRefreshService` reads these timestamps and fires a single catch-up refresh for any tile whose interval has elapsed since the app was last running — without double-firing tiles that shut down normally mid-interval.

A tile folder without a sidecar is treated as a static tile; rendering falls back to the output file extension (`.md` → markdown, `.json` → table/kpi-grid auto-detected, anything else → raw text).

**Startup migration**: `DashboardRefreshService` runs `DashboardService.MigrateAsync` at startup to convert legacy flat-file tiles (`<slug>.<ext>` + `<slug>.<ext>.yaml`) into the folder layout.

## Key components
- `KittyClaw.Core/Services/DashboardService.cs` — reads tile folders under `.dashboard/`, persists tile layout (position, size) keyed by slug in the per-project SQLite DB, and exposes add/remove/move/resize/read/write/migrate operations. Path helpers: `GetTileDirPath`, `FindScript`, `FindOutputPath`, `GetAvailableSlugs`, `WriteOutputAsync`, `DeleteTileFolder`, `MigrateAsync`.
- `KittyClaw.Core/Services/DashboardRefreshService.cs` — background service that polls tiles with a `refresh` sidecar field, enqueues them through `DashboardTileGate`, runs startup migration, and orchestrates the script-then-prompt pipeline before writing the output file.
- `KittyClaw.Core/Services/DashboardTileGate.cs` — global singleton semaphore (size 1) that serializes all tile refreshes across all projects. Scheduling: oldest `lastFinishedAt` first; never-run tiles last; manual refreshes jump the queue. Deduplicates by `(slug, tileSlug)`. Persists `lastFinishedAt` to `registry.db`.
- `KittyClaw.Core/Services/DashboardScriptRunner.cs` — executes tile scripts (`.ps1`, `.sh`, `.js`, `.py`), capturing stdout as the new tile content. Working directory is the project workspace root.
- `KittyClaw.Core/Services/TileRenderer.cs` — converts tile output content into HTML based on the tile template type (Markdown, KPI, Gauge, Heatmap, Timeline, BarChart, Donut, Sparkline, etc.).
- `KittyClaw.Core/Services/TileTemplate.cs` — catalogue of tile template variants (`markdown`, `kpi`, `kpi-grid`, `bar-chart`, `donut`, `gauge`, `heatmap`, `timeline`, `sparkline`, `progress`, `status-grid`, `leaderboard`, `image`, `mermaid`, `table`). Each variant defines its expected JSON or Markdown schema and the format instructions appended to LLM prompts.
- `KittyClaw.Core/Services/TileSidecar.cs` — reads/writes `tile.yaml` inside a tile folder.
- `KittyClaw.Web/Components/Pages/Dashboard.razor` — Blazor page rendering tiles on a 20 px dot-grid with free drag-and-drop (mouse events), resize handles, a chat-based AI tile creation panel, and a refresh log drawer.
- `KittyClaw.Web/wwwroot/js/dashboard.js` — client-side drag/resize helpers.
- `KittyClaw.Web/wwwroot/app.css` — dashboard-specific layout and tile styles.

## Entry points
- **UI**: "Dashboard" tab in the project topbar, alongside the Kanban view.
- **REST API** (all under `/api/projects/{slug}/dashboard/`):
  - `GET  /tiles` — list tiles with layout.
  - `POST /tiles` — register an existing `.dashboard/<tileSlug>/` folder as a tile (low-level: does not create the folder or sidecar).
  - `DELETE /tiles/{tileSlug}` — remove from layout AND delete the entire `.dashboard/<tileSlug>/` folder. Pass `?keepFiles=true` to only unregister.
  - `PATCH /tiles/{tileSlug}/position` — move a tile (`x`, `y`).
  - `PATCH /tiles/{tileSlug}/size` — resize a tile (`width`, `height`).
  - `GET  /tiles/{tileSlug}/output` — return the text content of the tile's output file. Use `/output/raw` for binary content.
  - `GET  /tiles/{tileSlug}/output/raw` — serve raw bytes (used by the `image` template).
  - `PUT  /tiles/{tileSlug}/output` — overwrite the output file (body = raw text; extension determined by template).
  - `GET  /tiles/{tileSlug}/sidecar` — return the parsed `tile.yaml` sidecar.
  - `PUT  /tiles/{tileSlug}/sidecar` — create or replace `tile.yaml` (body = JSON `TileSidecar`). Validates that `template` is a known id.
  - `GET  /tiles/{tileSlug}/script` — return the detected script filename (`script.*`) if present; 404 if none.
  - `POST /tiles/{tileSlug}/refresh` — trigger an immediate refresh (runs script then prompt).
- **Agent writes**: agents create `.dashboard/<tileSlug>/` folders with `tile.yaml` and `output.*` directly; the UI discovers them on next load.
- **Chat-based tile creation**: a conversational AI panel in the UI guides the user through creating a new tile — Claude asks follow-up questions then pre-fills a review popup before writing the folder.

## External dependencies
- [Storage](./storage.md) — tile layout persisted in the per-project SQLite DB.
- [REST API](./rest-api.md) — tile manipulation endpoints registered in `Endpoints.cs`.
