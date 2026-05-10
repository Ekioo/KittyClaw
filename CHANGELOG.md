# Changelog

All notable changes to KittyClaw.

## [v0.5] — 2026-05-10

Customizable dashboards, AutomationEngine refactor, architecture docs.

### Added
- Customizable per-project **dashboard** view with `.dashboard/` files, REST tile API, and live tile rendering.
- **Chat-based tile creation** via AI with spinner and format instructions.
- **Auto-refresh dashboard** files via scheduled LLM prompts.
- Tile **edit button**, custom titles, and heatmap label polish.
- Cross-project ticket references using `#{slug}:{id}` syntax.
- **Documentalist** agent in the project template; new `Agents/` folder name (was `.agents/`).
- Dedicated `consolidateAgentMemory` action with externalized instructions.
- Compile-time completeness check for automation node types.
- Current model displayed in LOG and chat window headers.
- New `doc/` folder with per-feature architecture pages.
- Sort projects by name with context-menu options.
- New automations now persisted immediately, but disabled by default.
- API actions in QaRunner scenarios.

### Changed
- `AutomationEngine` split into focused components (`ActionExecutor`, `ProjectRuntimeManager`).
- `ClaudeRunner` split into `ProcessLifecycleManager` + `ClaudeStreamPump`.
- New-project template moved into top-level `ProjectTemplate/`.
- API: `author` field clarified as required on mutating endpoints (HTTP 400 if omitted); `agent:` prefix dropped from author convention.

### Fixed
- Mermaid tile SVG fills its tile and scales with resize.
- Outside-click no longer dismisses edit modals.
- Snapshot `_events` list before iteration in `AgentRunDrawer`.
- Web host URL fallback propagation (HTTP-only on :5000 when unconfigured; `--urls` CLI arg honored; HTTPS redirection/HSTS removed).
- QaRunner isolated from real-claude dispatch.

---

## [v0.4] — 2026-05-08

End-to-end QA runner, mock claude CLI, publish tooling.

### Added
- **`KittyClaw.QaRunner`** — Playwright-based end-to-end QA runner (isolated test instance + scenario runner).
- **`KittyClaw.ClaudeMock`** — mock `claude` CLI for token-free dogfooding and hermetic agent dispatch.
- `tools/publish-stable.ps1` — publish Web + QaRunner + ClaudeMock as siblings.
- `KITTYCLAW_DATA_DIR` override for isolated instances; `KITTYCLAW_API_URL` injected into agent skills.
- QA launch profile on port 5231 with an isolated data dir.
- Per-project quota fallback model.

### Fixed
- UTF-8 forced on `claude` subprocess stdin/stdout/stderr; UTF-8 mangling repaired in skill templates.
- QaRunner: CSS rendering restored, onboarding skipped, switched to `Load` (not `NetworkIdle`); `togglePause` endpoint corrected.
- Default host port 5230 for published builds.

### Changed
- Pause button styled orange (`#f59e0b`) on paused projects.
- Linux-only paths fixed in agent skills; `qa-tester` now required to run the app.

---

## [v0.3] — 2026-05-04

Chat with agents, run history, demo & early-access launch.

### Added
- **Chat** with agents: persistent messages, session management, target selection, SSE stream reattachment with optional timestamp filter, stop button for active runs.
- **Run history** drawer with related UI components.
- Per-ticket "updated" indicator that clears only on open ([#95](https://github.com/Ekioo/KittyClaw/pull/95)).
- `createTicket` automation action with localization and UI.
- `RunConcurrencyGate` to manage simultaneous `claude` subprocesses.
- Multiple-assignee support for the assignee-resume automation.
- Retry mechanism for session restoration on resume failure.
- Image paste support in the create-ticket popup.
- Confirmation dialogs for deleting members, columns, labels.
- `GetNextRunTimes` and next-run-time display in the UI.
- Demo video and early-access / demo-site links in the README.

### Changed
- Built-in `Memory` tool disabled to prevent divergent memory sources for agents.
- "Owner" member auto-seeded for new and legacy projects.

### Fixed
- Improved ticket-update detection (last-seen timestamps).
- Better error handling for loading automation configurations and `ClaudeRunner` empty-body cases.

---

## [v0.2] — 2026-04-23

Project rebrand to **KittyClaw**, agentic engine, onboarding.

### Added
- **Renamed `Todo` → `KittyClaw`** across solution, projects, and namespaces.
- **Onboarding** modal and project-creation workflow with workspace setup.
- **`AgentsTemplateService`** + embedded `ProjectTemplate/` written into each new workspace.
- Initial agent roster: code-janitor, committer, evaluator, groomer, producer, programmer, qa-tester (skills + memory).
- Persistent memory system for agents (`memory.md` per agent) with `commitAgentMemory` action.
- **Automation engine** replacing per-project `dispatcher.mjs`:
  - Visual automations editor with custom drag-and-drop.
  - Node library: triggers (`TicketInColumn`, `GitCommitTrigger` with file watcher + `ignoreAuthors`, `Interval`), conditions (`HasParent`, `NoPendingTickets` with `concurrencyGroup`, `TicketCountInColumn`, `allSubTicketsInStatus`, `sameAssignee`), actions (`runAgent`, `commitAgentMemory`, `executePowerShell`).
  - Live agent-run spinner on tickets + SSE drawer with collapsible message blocks, human-readable tool calls, Markdown rendering.
  - Agent run logs persisted to disk across restarts; "last run" + log button on completed runs.
  - Urgent firing queue + `ITrigger.TryHandleExternalSignal`; respects `IsPaused`.
- **Sub-tickets** with parent-child relationships, `parentId` filter, sub-ticket status chips on cards.
- **Pause/Play** toggle per project (persisted, i18n).
- **Centralized project settings** page; expose `automations`, `runs`, `browse`, `skills` endpoints.
- **i18n (FR/EN)** services + user preferences; per-view `LocalizationService` JSON files.
- Per-project `WorkspacePath` for local repo binding; workspace health check.
- Undo with keyboard shortcut.
- `Todo.Core.Tests` xUnit project (67 tests).
- `MIT` License + initial `README.md`.
- `run.bat` / `run.sh` for one-shot launch with hot reload.
- New logos and onboarding visuals.

### Changed
- Default column `OwnerReview` → `Review` for new projects.
- Drag from handle only; drawer autoscroll.
- `.agents/` runtime state ignored from git.

### Fixed
- Database initialisation; `commitAgentMemory` actually git-commits the memory file; `{assignee}` placeholder resolved.
- Sub-ticket statuses load regardless of parent-status filter.
- Persist claude sessions for ticket-less agents.

---

## [v0.1] — 2026-03-27

First public release. Basic kanban with REST API.

### Added
- Blazor Server + .NET kanban app (`Todo.Core`, `Todo.Web`).
- Project registry + per-project SQLite databases.
- Models: `Project`, `Ticket`, `Comment`, `TicketStatus`.
- Services: `ProjectService`, `TicketService`.
- REST API endpoints (`Api/Endpoints.cs`) — see `API.md`.
- Board page with reconnect modal, error/404 pages.
