# Dashboard

## Purpose
A free-form, tile-based dashboard view that complements the Kanban board. Each tile displays a file read from the project's `.dashboard/` folder. Users and agents can add, edit, remove, move, and resize tiles; layout is persisted per project so it survives restarts. Agents write files directly to `.dashboard/`; the dashboard discovers them automatically.

Each tile result file is paired with a **sidecar YAML** at `<file>.yaml` containing five fields:
- `template` — required — which renderer to use (`markdown`, `table`, `kpi`, `kpi-grid`, `progress`, `sparkline`, `bar-chart`, `donut`, `gauge`, `status-grid`, `heatmap`, `leaderboard`, `timeline`, `image`, `mermaid`).
- `title` — optional display title shown in the tile header (falls back to the file name when absent).
- `refresh` — auto-refresh interval in seconds. `0` = static (no auto-refresh; can still be regenerated on demand by clicking the refresh button if a prompt is set).
- `prompt` — the LLM instruction sent to `claude` to (re)generate the file. Empty for fully static tiles.
- `model` — optional Claude model override (`""` = project default).

A file without a sidecar is treated as a static tile; rendering falls back to the file extension (`.md` → markdown, `.json` → table/kpi-grid auto-detected, anything else → raw text).

`DashboardRefreshService` polls every ~10 s and re-runs the prompt for tiles whose sidecar declares `refresh > 0` and the interval has elapsed. The agent's output is written back to the result file; the sidecar is left untouched. Manual refresh from the tile header always runs the prompt when one is set, regardless of `refresh`.

Legacy in-file front-matter (`---\nrefresh: …\nprompt: …\n---`) is **migrated automatically** the first time `ReadSidecarAsync` is called on such a file: the header is stripped from the file body and a `<file>.yaml` sidecar is created with `template: markdown` (for `.md`) or `template: table` (for `.json`).

## Key components
- `KittyClaw.Core/Services/DashboardService.cs` — reads `.dashboard/*.md` files, parses YAML front-matter, persists tile layout (position, size) in the per-project SQLite DB, and exposes add/remove/move/resize/refresh operations.
- `KittyClaw.Core/Services/DashboardRefreshService.cs` — background service that polls tiles with a `refresh` front-matter field, dispatches `claude` CLI calls, and writes updated content back to disk.
- `KittyClaw.Core/Services/TileRenderer.cs` — converts tile file content into HTML based on the tile template type (Markdown, KPI, Gauge, Heatmap, Timeline, BarChart, Donut, Sparkline, etc.).
- `KittyClaw.Core/Services/TileTemplate.cs` — catalogue of tile template variants (`markdown`, `kpi`, `kpi-grid`, `bar-chart`, `donut`, `gauge`, `heatmap`, `timeline`, `sparkline`, `progress`, `status-grid`, `leaderboard`, `image`, `mermaid`, `table`). Each variant defines its expected JSON or Markdown schema and the format instructions appended to LLM prompts.
- `KittyClaw.Core/Services/TileSidecar.cs` — reads/writes the YAML front-matter sidecar alongside tile files.
- `KittyClaw.Web/Components/Pages/Dashboard.razor` — Blazor page rendering tiles on a 20 px dot-grid with free drag-and-drop (mouse events), resize handles, a chat-based AI tile creation panel, and a refresh log drawer.
- `KittyClaw.Web/wwwroot/js/dashboard.js` — client-side drag/resize helpers.
- `KittyClaw.Web/wwwroot/app.css` — dashboard-specific layout and tile styles.

## Entry points
- **UI**: "Dashboard" tab in the project topbar, alongside the Kanban view.
- **REST API** (all under `/api/projects/{slug}/dashboard/`):
  - `GET  /tiles` — list tiles with layout.
  - `POST /tiles` — register an existing `.dashboard/` file as a tile (low-level: does not create the file or sidecar).
  - `DELETE /tiles/{fileName}` — remove from layout AND delete the file + sidecar. Pass `?keepFiles=true` to only unregister.
  - `PATCH /tiles/{fileName}/position` — move a tile (`x`, `y`).
  - `PATCH /tiles/{fileName}/size` — resize a tile (`width`, `height`).
  - `GET  /content/{fileName}` — return the textual content of a tile file (markdown, JSON, mermaid…). Use `/files/{fileName}` for binary content.
  - `PUT  /content/{fileName}` — overwrite the textual content (body = raw text).
  - `GET  /sidecar/{fileName}` — return the parsed YAML sidecar.
  - `PUT  /sidecar/{fileName}` — create or replace the sidecar (body = JSON `TileSidecar`). Validates that `template` is a known id.
  - `GET  /files/{fileName}` — serve raw bytes (used by the `image` template, also any binary).
- **Agent writes**: agents drop `.md` (or template-specific JSON/Mermaid) files into `.dashboard/` directly; the UI discovers them on next load.
- **Chat-based tile creation**: a conversational AI panel in the UI guides the user through creating a new tile — Claude asks follow-up questions then pre-fills a review popup before writing the file.

## External dependencies
- [Storage](./storage.md) — tile layout persisted in the per-project SQLite DB.
- [REST API](./rest-api.md) — tile manipulation endpoints registered in `Endpoints.cs`.
