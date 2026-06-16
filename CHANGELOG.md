# Changelog

All notable changes to KittyClaw.

## [v0.8] — 2026-06-16

Escape-key coverage, scroll preservation, real AskUserQuestion schema, and a much tighter agent process lifecycle.

### Highlights

This release finishes the Escape-key story started in v0.7: fullscreen editors now share a native confirm modal with dirty-check, the URL-loaded ticket panel is properly wired into the Escape stack, and handlers are re-registered after cancel so the second press still works.

The AskUserQuestion chat widget moves from prototype to production: it consumes the real CLI schema (`questions[].options[].label`), exposes an `IsAwaitingUserAnswer` flag, has a steering timeout, and a long-standing `SteeringQueue` race that swallowed mid-turn answers is fixed. The widget now renders with proper CSS variables instead of stray hex colors.

Agent process lifecycle gets two important fixes: claude subprocess trees are confined to a Win32 **job object** so a run can never leak children, and a force-kill kicks in after the `result` event if the process refuses to exit — no more hangs holding workspaces hostage. Chained `runAgent` actions (notably the judge) are now correctly dispatched in post-run processing.

The board preserves column scroll position on ticket open/close, the project delete control is relocated from the home card to a proper **danger zone** in ProjectSettings, and agent memory adopts a per-topic index layout (with the legacy `memory.md` still injected during the migration window).

### Added
- **Claude Opus 4.8** and **4.8-1M** model support across ActionEditor, Dashboard, and ProjectSettings.
- **Real AskUserQuestion CLI schema**: `questions[].options[].label` with `question`/`choices` aliases.
- **`IsAwaitingUserAnswer` flag** + steering timeout for AskUserQuestion turns.
- **Column scroll preservation** on ticket open/close via `board.js`.
- **Danger zone** in ProjectSettings: delete relocated from the home card.
- **Adversarial testing step** in the qa-tester skill.
- **Per-topic memory index**: `memory/MEMORY.md` scored index + on-demand topic files; native auto-memory disabled for agents.
- **README Dashboard section** with tile catalog and screenshot.

### Changed
- **EscapeKeyStack** wired into fullscreen editors (push in `OpenFullscreen` with dirty-check, dispose in Cancel/Save) and into the URL-loaded ticket panel.
- **Fullscreen ESC** uses an integrated native Blazor confirm modal; handler re-registered after cancel.

### Fixed
- **SteeringQueue race** that dropped mid-turn answers to AskUserQuestion.
- **AskUserQuestion widget**: submit button restored, CSS variables instead of hex colors.
- **Claude subprocess tree confined to a Win32 job object** so runs never leak children or hang the workspace.
- **Force-kill claude** after its `result` event when the process refuses to exit.
- **Chained `runAgent`** (judge) correctly dispatched in post-run action processing.
- **Legacy `memory.md` still injected** when present, to avoid recall loss mid-migration.
- **`board.js` loaded** so the column scroll-save JS interop resolves.

---

## [v0.7] — 2026-05-26

Agentic chat polish, dashboard reliability, and tag-based versioning.

### Highlights

This release turns the chat drawer into a real conversational surface: you can now steer agents mid-thinking, answer their questions as interactive bubbles, paste images, and resume runs that hit the max-turns ceiling — with messages that never silently drop on the floor.

The dashboard side becomes durable: tile refreshes and trigger runs persist their last-run timestamp and catch up after a restart, paused projects no longer waste cycles, and a friendly frequency picker covers the common "every N minutes / daily at HH:MM" cases.

Under the hood, versioning now flows from git tags via MinVer — which is exactly what made this release possible without touching a single csproj — and the automation engine has been split into a `TriggerHandler` + `RunStateManager` pair for easier reasoning.

Escape-key handling makes progress: the label and member managers now close on Escape with focus restored, and the legacy label/member buttons have been removed from the Board view. Several popups (ticket edition, title/description editors, tile add and edit, run history after navigating into an agent) still need wiring — expect more coverage in the next release.

### Added
- **Real-time steering**: inject text mid-thinking; messages dropped mid-turn are auto-replayed on the next turn.
- **AskUserQuestion bubbles** rendered as interactive prompts in the chat drawer.
- **Continue banner** when an agent hits max-turns, with one-click resume.
- **Image paste support** in the chat drawer.
- **Per-ticket worktrees**: helper scripts and a `{ticketId}` placeholder in `concurrencyGroup`, `mutuallyExclusiveWith`, and PowerShell args.
- **Per-ticket chain serialization** with debounce-on-completion to avoid duplicate runs.
- **Retry button** on the agent run drawer for failed runs.
- **Quota fallback model** triggered on rate-limit and usage-limit events.
- **Persist dashboard tile state**: `LastRefreshedAt` per tile with startup catch-up; same for interval/cron triggers via `LastRunAt`.
- **Pause-aware refresh**: skip dashboard tile refresh for paused projects.
- **Friendly frequency picker** for dashboard tiles, with daily-at scheduling.
- **Heatmap tile** enhanced with per-color intensity and an optional legend.
- **Escape key stack** broadened across popups (label/member managers included), with focus restoration.
- **Bidirectional column sort** via right-click context menu.
- **Agent running indicator** on project cards.
- **Release-update banner** with version compare and a dev simulate endpoint.
- **Markdown fallback** for deep content in chat; shared markdown pipeline now renders comment line breaks.
- `KITTYCLAW_TICKET_ID` env var exposed to agent subprocesses.

### Changed
- **Versioning via MinVer**: assembly version is derived from the latest `vX.Y.Z` git tag — no more manual csproj edits.
- **Endpoints split** into per-domain `Endpoints.*.cs` partial files.
- **AutomationEngine refactor**: extracted `TriggerHandler` and `RunStateManager`.
- **Member DELETE** cascade-clears assignments and protects the owner with HTTP 409.
- **OpenAPI**: typed response schemas, `Produces`/`ProducesProblem` annotations, `TicketSummary` vs `Ticket` distinction.
- **Legacy label/member management** buttons and popups removed from the Board view.
- **BoardFilterState** registered as scoped to isolate filter state per browser tab.

### Fixed
- `MainLayout` set to `InteractiveServer` rendermode to avoid a Body serialization crash.
- `FlattenJson` falls back to raw JSON when no body is extractable.
- `ReorderTicketAsync` now raises `TicketStatusChanged` when a column changes.
- `ticketInColumn` trigger now fires on unassigned tickets.
- `commitAsync` deferred until successful run completion to avoid partial commits on failure.
- Drop `--remote-control` and close stdin so claude runs don't deadlock; skipped entirely for chat sessions to prevent `payload.json` IPC conflict.
- `commitAgentMemory` uses the nested `.agents` git repo when present.
- PowerShell 5.1 fallback when `pwsh` is absent on Windows.
- Auto-continue chat run when steering messages are dropped mid-turn.

---

## [v0.6] — 2026-05-15

Dashboard tile pipeline overhaul, agent run robustness, and UX polish.

### Added
- **Script-first content pipeline** for dashboard tiles: tiles run a script that emits content, with UTF-8 stdout/stderr.
- **Folder-per-tile layout** with convention-based filenames under `.dashboard/`.
- **Global dashboard tile refresh semaphore** (size 1, LRU) to serialize refreshes and avoid concurrent claude sessions.
- Confirmation dialog before deleting an automation.
- `DashboardTileGate` documented in dashboard architecture docs.

### Changed
- `tile-chat` assistant raised to MaxTurns=25 and allowed to read existing files; now generates real `scriptContent` instead of a stub.
- README video replaced with YouTube thumbnail + animated WebP so it works in private browsing and across devices.

### Fixed
- Label remove button now visible on hover and no longer triggers ticket card click (#199).
- Prevent orphaned `Running` agent runs when `ClaudeRunner` pumps throw (#188).
- Dashboard tile refresh forces a fresh claude session each time so tools re-run instead of replaying.
- `TileSidecar.Prompt` and `Model` marked optional in the OpenAPI spec.

---

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
