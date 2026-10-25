# Dashboard

## Purpose
A free-form, tile-based dashboard view that complements the Kanban board. Each tile displays content read from the project's `.dashboard/` folder. Users and agents can add, edit, remove, move, and resize tiles; layout is persisted per project so it survives restarts. Agents write files directly into tile folders; the dashboard discovers them automatically.

The first-use guidance banner is shown only while the project has no tickets, runs, or dashboard tiles. Once activity exists, the activity summary replaces the banner regardless of activation-event history. The guidance directs users to the configured workflow without promising automatic provider selection or fallback.

The empty dashboard invites users to **Add tile** and offers clickable examples (tickets by column, recent Git activity, and a README summary). Examples open the creation chat with an editable request; sending it generates a proposal that must be reviewed before creation.

## Mobile presentation

At widths up to 767px, `css/mobile.css` displays tiles as a vertical list fitted
to the screen, removing overlaps and unused canvas space. Content can scroll
inside tall tiles. Desktop inline positions and sizes remain intact; drag and
resize entry points in `dashboard.js` ignore mobile layouts so reading a tile
cannot rewrite the saved desktop canvas. Tile selection and editing remain
available. See the [graphic charter](./graphic-charter.md) for responsive rules.

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

The sidecar YAML inside each tile folder contains six fields:
- `template` — required — which renderer to use (`markdown`, `table`, `kpi`, `kpi-grid`, `progress`, `sparkline`, `bar-chart`, `donut`, `gauge`, `status-grid`, `heatmap`, `leaderboard`, `timeline`, `image`, `mermaid`). The `heatmap` template supports per-color intensity levels and an optional legend via the `colors` and `legend` fields in the tile's JSON output.
- `title` — optional display title shown in the tile header (falls back to the slug when absent).
- `refresh` — auto-refresh interval in seconds. `0` = static (no auto-refresh; can still be regenerated on demand by clicking the refresh button if a script or prompt is set). Ignored when `refreshAt` is set.
- `refreshAt` — optional time-of-day trigger in `HH:mm` format (24-hour, zero-padded, e.g. `"07:30"`). When present, the tile fires **once per local day** at or after the specified time, regardless of `refresh`. Validated and normalised by `TileSidecarSerializer`; invalid values are silently dropped.
- `prompt` — optional LLM instruction sent to `claude` to (re)generate the output file. Omit for script-only tiles.
- `model` — optional Claude model override (`""` = project default).

Scripts are **not** declared in `tile.yaml`. Presence of a `script.*` file in the tile folder is the sole convention; the interpreter is resolved from the extension (`.ps1` → pwsh/powershell, `.sh` → bash/Git Bash, `.js` → node, `.py` → python).

### Content pipeline

`DashboardRefreshService` polls every ~10 s for tiles that need refreshing. Two scheduling modes are supported:

- **Interval mode** (`refresh > 0`): fires when `(now − lastRefresh) >= refresh` seconds.
- **Daily-at mode** (`refreshAt` set): fires once per local calendar day at or after the configured `HH:mm` time. Scheduling logic is delegated to `DashboardRefreshScheduling.ShouldFireDailyAt`, which is kept side-effect-free for testability.

When triggered it runs: **script first** (if `script.*` exists), then **prompt** (if set). Results are written to `output.*`. Manual refresh from the tile header works for any tile that has a script or a prompt, regardless of `refresh`. **Paused projects are skipped** — both the periodic tick and manual refresh calls return immediately without updating `lastRefreshed` if the project's `IsPaused` flag is set or if the project slug is unknown.

The last refresh timestamp is persisted per tile in a `dashboard_tile_refresh_state` SQLite table (in the per-project DB). After the host reports `ApplicationStarted`, `DashboardRefreshService` reads these timestamps and fires a single catch-up refresh for any tile whose interval has elapsed since the app was last running — without double-firing tiles that shut down normally mid-interval. Daily-at tiles also participate in catch-up: if the target time has passed today and the tile has not yet fired today, it fires once during catch-up. **Paused projects are also skipped during startup catch-up**: after migration, `IsPaused` is checked before `LoadAndCatchUpAsync` is called.

A tile folder without a sidecar is treated as a static tile; rendering falls back to the output file extension (`.md` → markdown, `.json` → table/kpi-grid auto-detected, anything else → raw text).

**Startup migration**: `DashboardRefreshService` runs `DashboardService.MigrateAsync` after the HTTP host is accepting requests, converting legacy flat-file tiles (`<slug>.<ext>` + `<slug>.<ext>.yaml`) into the folder layout. `StartupWorkGate` keeps this migration and catch-up work behind the explicit host-start boundary.

## Key components
The open dashboard reloads tile output and sidecars when the refresh queue changes, including when a background refresh completes. It also rereads them every 30 seconds to pick up direct file writes and delayed worktree integrations. Image URLs include the output file's modification timestamp so updated images bypass the browser cache.

- `KittyClaw.Core/Services/DashboardService.cs` — reads tile folders under `.dashboard/`, persists tile layout (position, size) keyed by slug in the per-project SQLite DB, and exposes add/remove/move/resize/read/write/migrate operations. Path helpers: `GetTileDirPath`, `FindScript`, `FindOutputPath`, `GetAvailableSlugs`, `WriteOutputAsync`, `DeleteTileFolder`, `MigrateAsync`.
- `KittyClaw.Core/Services/DashboardRefreshService.cs` — background service that polls tiles with a `refresh` sidecar field, enqueues them through `DashboardTileGate`, runs startup migration, and orchestrates the script-then-prompt pipeline before writing the output file.
- `KittyClaw.Core/Services/DashboardTileGate.cs` — global singleton semaphore (size 1) that serializes all tile refreshes across all projects. Scheduling: oldest `lastFinishedAt` first; never-run tiles last; manual refreshes jump the queue. Deduplicates by `(slug, tileSlug)`. Persists `lastFinishedAt` to `registry.db`.
- `KittyClaw.Core/Services/DashboardScriptRunner.cs` — executes tile scripts (`.ps1`, `.sh`, `.js`, `.py`), capturing stdout as the new tile content. Working directory is the project workspace root.
- `KittyClaw.Core/Services/TileRenderer.cs` — converts tile output content into HTML based on the tile template type (Markdown, KPI, Gauge, Heatmap, Timeline, BarChart, Donut, Sparkline, etc.).
- `KittyClaw.Core/Services/TileTemplate.cs` — catalogue of tile template variants (`markdown`, `kpi`, `kpi-grid`, `bar-chart`, `donut`, `gauge`, `heatmap`, `timeline`, `sparkline`, `progress`, `status-grid`, `leaderboard`, `image`, `mermaid`, `table`). Each variant defines its expected JSON or Markdown schema and the format instructions appended to LLM prompts.
- `KittyClaw.Core/Services/TileSidecar.cs` — reads/writes `tile.yaml` inside a tile folder. `TileSidecarSerializer` normalises `refreshAt` on both read and write (strips invalid values).
- `KittyClaw.Core/Services/TileFrequency.cs` — UI-friendly frequency mapping. `TileFrequency.ToSidecar(kind, value, time)` converts a `(TileFrequencyKind, int, string?)` tuple to the `(RefreshSeconds, RefreshAt)` pair stored in `tile.yaml`; `FromSidecar(sidecar)` does the reverse. `TileFrequencyKind` values: `Never`, `Minutes`, `Hours`, `Days`, `DailyAt`.
- `KittyClaw.Core/Services/DashboardRefreshScheduling.cs` — pure-function helpers for daily-at scheduling. `ShouldFireDailyAt(now, lastRefresh, hhmm)` returns `true` when the target time has been reached today and the tile has not yet fired today. Side-effect-free; unit-tested independently.
- `KittyClaw.Core/Services/DashboardRefreshTiming.cs` — pure helper behind the header timing indicator. `Calculate(nowUtc, lastRefreshUtc, sidecar)` returns a `DashboardRefreshTimingState` (`SinceLastRefresh`, `UntilNextRefresh`, `IsDue`) covering interval, daily-at, and static tiles: a static tile (`refresh` = 0 and no `refreshAt`) gets a `null` `UntilNextRefresh` so no next-refresh is displayed. Side-effect-free; unit-tested independently.
- `KittyClaw.Web/Components/Pages/Dashboard.razor` — Blazor page rendering tiles on a 20 px dot-grid with free drag-and-drop (mouse events), resize handles, a chat-based AI tile creation panel, a refresh log drawer, and a **frequency picker** in the tile config popup. The picker presents labelled presets (Never / Every N minutes / Every N hours / Every N days / Daily at HH:mm) and maps them to sidecar fields via `TileFrequency`. Each tile header also shows a compact **refresh-timing indicator** right after the title (`↺` elapsed since last refresh · `↻` time until next refresh, with a localized tooltip), computed by `DashboardRefreshTiming` and re-rendered every second by a `PeriodicTimer` loop — no page reload. Static tiles omit the next-refresh part.
- `KittyClaw.Web/wwwroot/js/dashboard.js` — client-side drag/resize helpers.
- `KittyClaw.Web/wwwroot/css/dashboard.css` — dashboard-specific layout and tile styles.

## Entry points
- **UI**: "Dashboard" tab in the project topbar, alongside the Kanban view.
- **REST API** (all under `/api/projects/{slug}/dashboard/`):
  - `GET  /tiles` — list tiles, derived from `.dashboard/` folders on disk merged with any stored layout; no registration needed.
  - `DELETE /tiles/{tileSlug}` — remove from layout AND delete the entire `.dashboard/<tileSlug>/` folder.
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

## Output writes with worktrees enabled

`PUT /api/projects/{slug}/dashboard/tiles/{tileSlug}/output` writes the output in the
project's serialized maintenance worktree and returns HTTP 204 once the validated
change is committed and queued. At that point the primary checkout and its current
output remain unchanged. Integration publishes the commit to the isolated integration
tip; the separate local-checkout synchronization makes the new output available to
dashboard reads and rendering. Repeating an unchanged write reuses the maintenance
worktree without creating a review-required change.

Generated directory names are bounded to avoid Git for Windows' `$GIT_DIR too big`
failure with long project slugs; see [durable integrations](./durable-worktree-integrations.md)
for naming and existing-worktree compatibility.

`DashboardOutputWorktreeHttpTests` exercises the HTTP write and queued maintenance
output. `DashboardWorktreeJourneyTests` runs the real application on an ephemeral local
port and checks initial rendering, the 204 response, isolation before integration,
the integrated Git content, synchronization by the actual background worker after
project resume, updated browser rendering, and an unchanged repeated write. It uses
synthetic data and no agent provider. Run it with:

```powershell
$env:KITTYCLAW_DASHBOARD_WORKTREE_E2E = '1'
dotnet test KittyClaw.Core.Tests --filter FullyQualifiedName~DashboardWorktreeJourneyTests
```

Playwright Chromium must be installed; alternatively set
`KITTYCLAW_TEST_BROWSER_CHANNEL=msedge` to use an installed Edge browser. Screenshots
are written to the ignored `.artifacts/dashboard-worktree-e2e.png` file and are not
part of published documentation.

## Tile selection and editor keyboard behavior

The tile selector above the canvas lists every tile, including tiles fully covered
by another tile. Selecting a tile brings it to the foreground, scrolls it into view
and focuses its first available action. Pointer activation and keyboard focus also
activate tiles. This transient stacking order does not change persisted coordinates.

The configuration editor registers with the shared Escape stack on every entry
(edit or AI proposal), moves focus inside after render, and contains Tab navigation.
Escape closes the topmost editor and restores the original trigger's focus. Save,
cancel, revise and component disposal release the Escape registration.

Saving a tile from the configuration popup automatically commits its configuration, script and initial output together. Shared-checkout commits include only that tile folder and preserve unrelated staged changes. With worktrees enabled, the save uses the maintenance worktree and queues integration; the dashboard picks it up after synchronization. Commit errors leave the editor open and display the failure.
