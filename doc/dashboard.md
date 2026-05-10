# Dashboard

## Purpose
A free-form, tile-based dashboard view that complements the Kanban board. Each tile displays a Markdown file read from the project's `.dashboard/` folder. Users and agents can add, remove, move, and resize tiles; layout is persisted per project so it survives restarts. Agents write files directly to `.dashboard/`; the dashboard discovers them automatically.

## Key components
- `KittyClaw.Core/Services/DashboardService.cs` — reads `.dashboard/*.md` files, persists tile layout (position, size) in the per-project SQLite DB, and exposes add/remove/move/resize operations.
- `KittyClaw.Web/Components/Pages/Dashboard.razor` — Blazor page rendering tiles on a 20 px dot-grid with free drag-and-drop (mouse events) and resize handles.
- `KittyClaw.Web/wwwroot/js/dashboard.js` — client-side drag/resize helpers.
- `KittyClaw.Web/wwwroot/app.css` — dashboard-specific layout and tile styles.

## Entry points
- **UI**: "Dashboard" tab in the project topbar, alongside the Kanban view.
- **REST API** (all under `/api/projects/{slug}/dashboard/`):
  - `GET  /tiles` — list tiles with layout.
  - `POST /tiles` — add a tile by file name.
  - `DELETE /tiles/{fileName}` — remove a tile.
  - `PATCH /tiles/{fileName}/position` — move a tile (`x`, `y`).
  - `PATCH /tiles/{fileName}/size` — resize a tile (`width`, `height`).
- **Agent writes**: agents drop `.md` files into `.dashboard/` directly; the UI discovers them on next load.

## External dependencies
- [Storage](./storage.md) — tile layout persisted in the per-project SQLite DB.
- [REST API](./rest-api.md) — tile manipulation endpoints registered in `Endpoints.cs`.
