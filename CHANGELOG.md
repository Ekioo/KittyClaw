# Changelog

All notable changes to KittyClaw.

## [Unreleased]

- Added native Mistral Vibe support with `mistral:*` model routing, resumable provider-scoped sessions, streaming activity normalization, onboarding detection, model selectors, API discovery, version probing, documentation, and rate-card entries.
- Added a cross-platform in-app workspace browser with roots, breadcrumbs, parent navigation, and direct path entry for project creation and workspace settings.
- Clarified new-project setup throughout the wizard and documentation: it analyzes existing folders, asks targeted questions for empty workspaces, and creates an approved workflow without presenting the operation as a legacy migration.
- Preserved tickets already in semantic success columns during legacy workflow migration and visually distinguished allowed and forbidden routing destinations during drag-and-drop.

## [v0.13] — 2026-08-04

Visual project pipelines, deterministic column processors, guided workflow migration, human hand-off guidance, and a broad reliability and interface polish pass.

### Highlights

KittyClaw projects can now contain **multiple independently named pipelines** backed by stable identifiers. Each column can own one deterministic processor with a mission, model, prompt, skills, ordered actions, schedules, routing rules, retries, and persistent memory. Processor and skill definitions live in the project workspace so workflows are reviewable, versioned, and reproducible through Git.

Legacy automation boards gain a **guided visual migration wizard**. KittyClaw analyzes the existing board, proposes distinct pipelines and columns, lets the user refine every stage conversationally, executes a resumable migration plan, and disables migrated legacy automations. New projects receive the same workflow-planning assistance, including targeted questions when the workspace is empty.

Human hand-offs are now first-class workflow states. Waiting columns can declare owner-action guidance, tickets clearly show what the user must do to unblock them, and manual moves are constrained by the processor's existing routing rules so users cannot accidentally skip required gates.

### Added

- Multiple stable-key pipelines per project, with pipeline-aware URLs, navigation, unread counts, duplication, renaming, and cross-pipeline ticket moves.
- Column processors with project-file persistence, dedicated prompts and memory, selectable Claude/Codex/Grok/Ollama models, recommended or required skills, bounded retries, ordered actions, scheduled tasks, and conditional routing.
- Durable non-agent processor actions for scripts, HTTP requests, ticket updates, comments, child-ticket creation, and column moves.
- Visual scheduled-task editor and column-local scheduling, replacing raw cron entry for normal workflows.
- Right-click column and ticket menus for configuration, insertion, duplication, sorting, marking read, and moving tickets between project pipelines.
- Pipeline-aware waiting guidance and the distinct owner-action role, visually highlighting tickets that require human intervention.
- Guided migration and new-project workflow wizards with graphical pipeline/column proposals, iterative prompting, progress feedback, resumable execution, and completion validation.
- Provider-aware onboarding checks for Claude, Codex, Grok, and Ollama.
- Claude Opus 5 model selection alongside Fable 5, Sonnet 5, active Opus 4.x models, and Haiku 4.5.
- Tips covering pipelines, processors, routing, schedules, migration, owner hand-offs, and contextual board controls.

### Changed

- Workflow configuration is centered on the kanban: column structure, processor, actions, schedules, and routing are edited from one contextual dialog.
- Processor routing is the single source of truth for both automatic outcomes and allowed manual moves.
- Conversation creation remembers the last chosen model while each established conversation keeps its own model.
- Pipeline tabs share the board's visual language, remain vertically balanced, and expose visible unread-ticket counts without counting hidden child tickets.
- Project and column menus, keyboard focus states, tooltips, loading indicators, and workflow screens use harmonized interaction styles.
- Legacy automations remain compatible for existing projects but migrated workflows disable their replaced automations.

### Fixed

- Prevented processor retry and routing loops, duplicate dispatches, inherited Codex notifications, memory-commit retriggers, and concurrent ownership races.
- Paused projects no longer execute column processors or scheduled workflow tasks, and resumable work recovers safely after restart.
- Parent tickets now resume correctly when blocking children reach pipeline-defined success columns, including shared success columns.
- Ticket drawers preserve the selected secondary pipeline, remain mounted during URL updates, and no longer flash or break stale Blazor circuits.
- Conversation history renders immediate loading feedback, discovers providers in parallel, avoids loading the same history twice, and animates its progress indicator.
- Oversized and legacy chat sessions recover their persisted model and resume safely after provider or application restarts.
- Column processor schema migration runs before claims against older project databases.
- Static web assets are replaced cleanly during stable publication so stale immutable browser caches cannot serve truncated scripts.
- Scheduled routes survive renamed columns and are cleaned when columns are deleted.
- Codex and Grok token costs are estimated when their CLIs omit a monetary total.

---

## [v0.12] — 2026-08-01

OpenAI Codex support, a unified multi-project home, lossless cross-project ticket transfers, expanded localization, and safer concurrent board updates.

### Highlights

KittyClaw now supports the **OpenAI Codex CLI** alongside Claude and Grok while preserving chat context across provider changes and falling back cleanly when a configured model is unavailable.

The home screen is now a **unified multi-project workspace** with project-card and kanban-swimlane modes, plus an extracted ticket-panel overlay that keeps navigation and unread state consistent across views.

Tickets can be **transferred losslessly between projects** through an atomic cross-database operation. Ticket and history identifiers, immutable timestamps, comments, activities, labels, relationships, schedules, assignments, and token/cost metadata are preserved; unsupported mappings and collisions are rejected without mutating the source. Transfer-audit IDs are allocated above both databases so sequential migrations remain safe.

### Added

- **OpenAI Codex CLI backend** with provider-aware session handling and model fallback.
- **Unified multi-project home** with project cards and kanban swimlanes.
- **Lossless cross-project ticket transfer API** with preflight validation, atomic rollback, provenance audit events, and sequential-transfer regression coverage.
- **Spanish, German, and Italian localization**, extending the existing English and French UI.
- **Tip of the day** on the unified home.
- **Onboarding and ticket-scheduling documentation**.

### Changed

- **License: MIT → AGPL-3.0-or-later.** All versions after v0.11 are licensed under the GNU Affero General Public License v3 or later; versions up to and including v0.11 remain available under MIT. A new `NOTICE.md` documents the license history and grants an explicit additional permission: the `ProjectTemplate/` files copied into user workspaces stay MIT-licensed, and everything the app produces (tickets, logs, agent memory, commits) carries no license obligation — initializing a project with KittyClaw never places it under the AGPL.
- **Anti-silent-rebrand terms (AGPL §7)**: derivative works must preserve the KittyClaw attribution (license notices, the new in-app legal-notice footer on the home page, and a "based on KittyClaw" statement in their README), must not misrepresent their origin (§7(c)), and receive no trademark rights to the KittyClaw name or logos (§7(e)).
- **Atomic ticket updates**: multi-field ticket PATCH operations apply in one write, support expected-status concurrency checks, and reject unknown fields instead of silently ignoring them.
- **Merge-safe label updates** prevent concurrent writers from replacing each other's ticket labels.
- **SQLite reliability**: every connection uses WAL mode and a busy timeout.

### Fixed

- Cross-project transfer audit activities can no longer consume identifiers still used by source-project history.
- Unread ticket state remains per-ticket and survives board refreshes and moves.
- Large stderr output is collapsed in the run drawer instead of overwhelming the interface.
- Chat context survives provider changes, and unavailable configured models fall back correctly.
- `statusChange` snapshots are isolated per automation, preventing one workflow from consuming another workflow's transition.
- Post-run action chains execute exactly once instead of silently skipping `createTicket` or `moveTicketStatus` actions.
- Project workspace paths are validated before being persisted.

### Security

- Project workspace paths are constrained and validated at write time.
- Allowed hosts are pinned to local loopback names.
- The repository is now licensed under **AGPL-3.0-or-later** with explicit attribution and project-template exceptions documented in `NOTICE.md`.

---

## [v0.11] — 2026-07-30

macOS/Linux support, a second agent backend (Grok CLI), outbound webhooks, and a deep automation-engine reliability pass driven by real production incidents.

### Highlights

KittyClaw now **runs on macOS and Linux**: a DI binding bug made the app crash at startup on every non-Windows host (thanks to Pedro R Zabala / @FoodBreakPedro for the diagnosis and fix in PR #3), `run.sh` ships executable, and CI now builds and tests on Ubuntu alongside Windows so this class of regression can't come back.

Agents can now be dispatched through the **Grok CLI (xAI)** as an alternative backend: per-member/per-action model routing picks the right binary, Grok's streaming JSON is adapted onto the existing run drawer, sessions are namespaced per backend, and payment/quota errors feed the same quota-fallback machinery as Claude. The dispatch pipeline was renamed provider-neutral (`AgentRunner`, `AgentStreamPump`, `ChatDrawer`) to reflect it.

Automations gain an **`httpRequest` action** — outbound webhooks to Discord/Slack/CI with placeholder support — hardened against SSRF at the socket level: loopback/link-local/cloud-metadata targets are refused at connect time (DNS rebinding can't bypass it), redirects are disabled, responses are capped, and neither URLs nor header values are ever logged.

The engine went through a **reliability pass driven by four documented production outages** (tickets #112–#115): `automations.json` saves can no longer erase concurrently-added automations, the same owner comment can no longer re-fire an agent on every poll (up to 8 phantom runs observed), two column-poll automations can no longer race on the same ticket, and a reload can no longer silently unregister scheduled tasks. A new `GET /api/engine/health` endpoint makes any future silent outage visible: engine tick age, per-project registered/overdue schedules, and last-fired timestamps.

### Added

- **Grok CLI backend**: binary detection (incl. `~/.grok/bin`), `ModelRouting`, `GrokStreamAdapter` for streaming-json chunks, `GET /api/grok-models`, prompt delivery via `--prompt-file`, per-backend session namespacing, payment-error → quota signal, curated model fallback.
- **`httpRequest` automation action**: GET/POST/PUT/PATCH/DELETE with `{ticketId}`/`{ticketTitle}`/`{ticketStatus}`/`{assignee}` placeholders in URL, body and headers; per-action timeout and `abortOnFailure`; SSRF guard enforced in the connect callback with an explicit `allowLocalTargets` opt-in; full editor UI (en/fr).
- **`GET /api/engine/health`** (ticket #114): engine `startedAt`/`lastTickAt`/`lastTickAgeSeconds`, and per project the automation counts, registered cron schedules, next/overdue fire times, and last-fired automation — three independent signals that make a dead scheduler visible.
- **Optimistic concurrency for `automations.json`** (ticket #115): `GET …/automations` returns a `fileStamp`, `PUT` accepts `?baseStamp=` — a stale stamp triggers a conservative merge that preserves concurrently-added automations and reports `preservedIds`.
- **`{now}` and `{time}` placeholders** in `createTicket` title/description, alongside `{date}`/`{monday}`/`{firstOfMonth}`.
- **Quota parking**: an agent run that fails on provider quota parks its ticket in Blocked with a comment instead of looping.
- **Release tooling**: `RELEASING.md` ritual + `tools/publish-release.ps1` (zip build, MinVer/tag verification, GitHub release from the CHANGELOG entry).
- **CI**: `ubuntu-latest` added to the build-test matrix.

### Changed

- **Provider-neutral dispatch pipeline**: `ClaudeRunner` → `AgentRunner`, `ClaudeStreamPump` → `AgentStreamPump`, `ClaudeChatDrawer` → `ChatDrawer`; owner-chat target renamed "KittyClaw".
- **Round-trip-faithful automation config**: unknown/optional JSON fields (e.g. per-automation `model` pins) survive UI/API saves via `[JsonExtensionData]` across the spec hierarchy.
- **First-match-wins dispatch** (ticket #112): column-poll automations evaluate in file order and the first one that matches and dispatches consumes the ticket for that tick — order routing automations before dispatch ones.

### Fixed

- **App crashed at startup on macOS/Linux** (GitHub issue #2 / PR #3 by @FoodBreakPedro): `IFolderPicker` is now bound from DI with `[FromServices]`; `run.sh` ships with the exec bit.
- **`automations.json` saves erased concurrent edits** (ticket #115): saves now merge under a per-project lock with atomic write; divergences are logged with the preserved IDs.
- **Same comment re-fired agents on every poll** (ticket #113, up to 8 phantom runs in production): consumed-comment state is now per automation and persisted via an atomic monotonic merge; the urgent signal path consumes at dispatch time, after conditions, and survives reloads (ticket #136).
- **Column conditions never passed on the fast path** (ticket #135): signal firings carry no status snapshot — `ticketInColumn` conditions now resolve the live ticket status instead of always failing.
- **Reload could leave automations without registered triggers** (ticket #114): the new trigger map is built before swapping config in, so a failed reload keeps the previous coherent state.
- **Two column-poll automations raced on the same ticket** (ticket #112, tickets stuck for 2 days in production): per-tick per-ticket consumption ends the race.
- **Root `PATCH /tickets/{id}` silently dropped the `status` field**: it now returns 400 pointing to the dedicated `/status` endpoint (which validates the column and notifies automations).
- **Create/move into a non-existent board column is refused** instead of corrupting the board.
- **Empty comment returned an SQLite 500** — now a clean 400.
- **Quota fallback** preserves the configured fallback model across reloads and can fall back to any available model.

### Security

- **Prompt-injection spotlighting** (ticket #131): ticket-derived text (title, and by instruction description/comments) is wrapped in a delimited `<TICKET_UNTRUSTED>` block with a do-not-obey notice in every agent prompt; embedded delimiters are stripped recursively so fragments cannot reassemble and escape the block.
- **`httpRequest` SSRF hardening**: connect-time target validation (immune to DNS rebinding), loopback/link-local/metadata/multicast blocked by default, no redirects, capped response reads, secret-safe logging.

---

## [v0.10] — 2026-07-24

Scheduled tickets, per-ticket token cost, a rebuilt cron scheduler — and a deep security & reliability hardening pass.

### Highlights

Tickets can now be **scheduled**: park a ticket in the new "Scheduled" column with a fire date and a target column, and a background service auto-promotes it once due — calendar-dated work gets a dedicated home instead of polluting "Blocked". The schedule is visible and editable directly in the ticket panel, and scheduled cards show a countdown badge on the board.

Agent runs now report **what they cost**: token usage and USD cost are captured from the CLI, accumulated per run, and persisted as durable per-ticket totals — with badges on board cards, the ticket panel, and the run drawer, which also makes the daily budget gate real.

Interval/cron triggers were rebuilt around a **persisted NextRunAt schedule**: a restart that straddles the scheduled moment still fires on time, a missed occurrence catches up with one immediate fire, external edits to `automations.json` are picked up automatically, and raw cron text entry is replaced with a day/time picker. A **concurrency-lock dead man's switch** force-stops hung runs that would otherwise hold their group's lock forever, with a new endpoint listing currently-locked groups.

This release also lands a broad hardening pass: stored XSS in markdown rendering, path traversal in dashboard tiles and project/agent slugs, and unsafe image uploads are all fixed, two vulnerable transitive dependencies are pinned, and a series of concurrency defects (lost session-registry writes, a dashboard tile gate permit leak, engine-tick starvation, deadlock-prone subprocess runners) are resolved.

### Added

- **Scheduled tickets** (feature #99): "Scheduled" status/column with `FireAt` + target column, 30s auto-promotion service, `PATCH …/tickets/{id}/schedule` endpoint, countdown badge and soonest-first sort on the board.
- **Ticket panel schedule editor**: view/edit the schedule in local time, "Schedule…" button on non-scheduled tickets; moving a ticket out of Scheduled clears its schedule.
- **Per-ticket token cost**: usage and `total_cost_usd` captured from CLI result events, per-run accumulation, durable per-ticket totals and workspace cost-log; badges on board card, ticket panel, and run drawer; runs API exposes token fields.
- **Concurrency-lock dead man's switch** (ticket #98): opt-in per-automation inactivity timeout (`LockTimeoutMinutes`), per-run activity heartbeat, a reaper that force-stops idle runs, and `GET /projects/{slug}/concurrency-groups` for lock observability.
- **Anonymous daily usage heartbeat** to Umami Cloud: one event per instance per 24h (instance GUID, version, OS family), silent-fail, disabled in Development.
- **Isolated debug instance**: `kittyclaw-web-debug` launch config on :5232 with its own data dir and a mock claude CLI, so end-to-end verification never touches real projects.
- **CI**: GitHub Actions build + test workflow on every push/PR to main/dev, with the mock claude built explicitly for hermetic integration tests.
- **Memory index links**: agent `MEMORY.md` index lines are now markdown links to their topic files; the consolidation pass rewrites legacy lines on touch.

### Changed

- **Interval/cron triggers reworked** around a persisted `NextRunAt`: cron-only schedule computed at registration, restart-safe firing, one-shot catch-up of missed occurrences, automatic reload when `automations.json` changes on disk, day/time picker instead of raw cron text (legacy `Seconds` migrated to cron).
- **Dashboard tile registration is no longer required**: tiles appear as soon as their `.dashboard/<slug>/` folder exists; layout rows are created lazily on first move/resize.
- **MaxRunDuration contract honored**: chat sessions are no longer force-killed at 60 min; automation runs and memory consolidation get 30 min, dashboard tile refreshes 15 min, and `null` genuinely means no wall-clock timeout.
- **Column sort by due date replaced with modified date** — the due-date sort was a no-op (tickets have no due-date field); persisted sort preferences remain valid.

### Fixed

- **Duplicate board columns crashed the board**: self-healing dedupe migration, UNIQUE index on column names, idempotent column creation, rename-onto-taken-name refused, and the board now degrades instead of crashing on corrupted data.
- **SessionRegistry lost concurrent writes**: read-modify-write cycles are now atomic under the file lock, ending lost session IDs and regressed commit cursors.
- **DashboardTileGate permit leak**: a cancelled refresh winner permanently blocked all subsequent tile refreshes across all projects.
- **Engine tick starvation**: long inline automation actions (memory consolidation, PowerShell) detach to a background task instead of blocking every trigger in every project.
- **Ad-hoc subprocess runners consolidated** onto a hardened `ProcessRunner`: concurrent pipe drain (no more deadlocks), enforced wall-clock timeouts, process-tree kill, and a per-repository git semaphore so one slow repo no longer stalls all memory commits.
- **Multi-statement writes wrapped in transactions**: no more orphaned tickets after a crash mid column-rename/delete or member delete.
- **Migration overhead**: inline migrations now run once per database file instead of on every query, with new indexes on the board's hot paths; ALTER TABLE errors are no longer silently swallowed.
- **AgentRunDrawer** no longer kills the Blazor circuit with "Collection was modified" — event-buffer mutation moved onto the sync context.
- **Localization** falls back to English when a key is missing from the active language, and formats invariantly.
- **Test suite unhung**: steering tests no longer respawn mock subprocesses forever; full suite 472/472 in 17s.

### Security

- **Stored XSS**: all Markdig pipelines now `DisableHtml()`, and tile style attributes only accept validated CSS colors.
- **Path traversal** blocked on dashboard tile delete/move/resize/refresh (the delete sink validates the resolved path as defense in depth), and project/agent slugs are validated before touching the filesystem.
- **Image upload hardened**: real format sniffed from magic bytes (png/jpg/gif/webp only, no SVG), 10 MB cap, nosniff + sandboxing CSP on `/uploads/` responses.
- **Dependency pins**: SQLitePCLRaw 3.0.3 (CVE-2025-6965) and Microsoft.OpenApi 2.10.0 (GHSA-v5pm-xwqc-g5wc).

---

## [v0.9] — 2026-07-02

Ollama local model support, per-action model selection, and a centralized model catalog.

### Highlights

This release brings first-class local-model support: Ollama models are now selectable per-action and per-member through an OpenAI-compatible provider, with a model discovery endpoint and dedicated selectors in the chat drawer and member settings. Claude model support is centralized in a new `ClaudeModelCatalog`, which now also lists Fable 5 and Sonnet 5.

Reliability also improves: background agent runs get a longer default timeout, empty model selections no longer leak through as invalid state, and the `--disallowed-tools Memory` flag is no longer sent where it's a no-op or unsupported (Ollama models).

### Added
- **Ollama local model support** via an OpenAI-compatible provider, with a model discovery endpoint (`SaveLocalModelConfig`) and per-action / per-member model selectors.
- **`Member.DefaultModel`** with runtime model resolution used across chat and actions.
- **`ClaudeModelCatalog`**: centralizes the supported Claude model list; adds `claude-fable-5` and `claude-sonnet-5`.
- **Chat drawer model selector** (New Instruction), theme-consistent with the rest of the UI.
- **Streamed loading bubble**: `content_block_delta` text now streams directly into the chat drawer's loading bubble.
- **SSE error/stderr surfacing** in the chat drawer, with a forced new session on model change.
- **Kanban column pagination**: sorted columns load 20 tickets initially, 10 more per load-more.

### Changed
- Removed the "(default)" model option; unset selections fall back to `claude-sonnet-4-6`.
- Default background run timeout bumped from 30 to 60 minutes.
- Chat drawer stderr events are muted from the visible log; connectors warning suppressed.

### Fixed
- Empty model string now normalizes to `null` in `ActionExecutor` instead of leaking through as an invalid value.
- `--disallowed-tools Memory` dropped as a no-op flag, and skipped entirely for Ollama models.

---

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
