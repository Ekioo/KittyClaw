# Automation engine

## Purpose
Background service that watches each project for events and dispatches agents in response. Drives the agentic workflow: when a ticket moves, a comment is posted, a commit lands, an interval elapses, etc., the engine evaluates configured automations and runs the matching actions.

This is the legacy, backward-compatible automation system. New business-state workflows can use the separate [pipeline and column processing](./column-workflows.md) engine. Cron and interval triggers remain supported here.

## Key components
- `KittyClaw.Core/Automation/AutomationEngine.cs` — top-level wiring only; delegates to `TriggerHandler` and `RunStateManager`.
- `KittyClaw.Core/Automation/TriggerHandler.cs` — owns the tick loop (urgent drain + per-project poll).
- `KittyClaw.Core/Automation/RunStateManager.cs` — encapsulates the 5 dispatch-gate checks (`ShouldSkipAsync`); shared by `AutomationEngine` and `ActionExecutor`.
- `KittyClaw.Core/Automation/ActionExecutor.cs` — condition evaluation and all `Execute*Async` action implementations; delegates skip checks to `RunStateManager`. Holds `_inFlightChains` (`ConcurrentDictionary` keyed by `"{automationId}:{ticketId}"`) to serialize directly dispatched action chains per (automation, ticket) pair; queued `ticketInColumn` chains use the queue processor instead.
- `KittyClaw.Core/Automation/AutomationQueueStore.cs` — durable per-project SQLite FIFO for complete `ticketInColumn` action chains, including occurrence deduplication, leases, recovery, terminal states, and loop protection.
- `KittyClaw.Core/Automation/AutomationQueueProcessor.cs` — claims queued entries and runs each saved automation snapshot to completion. It allows up to eight workers per project while reserving `concurrencyGroup` and `mutuallyExclusiveWith` groups before execution.
- `KittyClaw.Core/Automation/ProjectRuntimeManager.cs` — per-project runtime dictionary and signal fan-out.
- `KittyClaw.Core/Automation/ProjectRuntime.cs` — data class holding per-project run state.
- `KittyClaw.Core/Automation/AutomationConfig.cs` — JSON-deserialized automation definitions (triggers, conditions, actions).
- `KittyClaw.Core/Automation/AutomationStore.cs` — loads/persists `automations.json` from each workspace's `.agents/` folder. Saves are merge-safe (ticket #115): under a per-project IO lock the file is re-read before writing, and any automation present on disk but missing from the payload is preserved unless the caller proves it edited the latest version by passing back the `fileStamp` (SHA-256 of the file) obtained at load. Divergences are logged as warnings. Unknown JSON fields round-trip via `[JsonExtensionData]`, so hand-added keys (e.g. custom pins) survive a UI save. Writes are atomic (temp file + rename).
- `KittyClaw.Core/Automation/Triggers/` — trigger implementations.
- `KittyClaw.Core/Automation/GitRepositoryWatcher.cs` — backs the `gitCommit` trigger.
- `KittyClaw.Core/Automation/RunConcurrencyGate.cs` — serializes runs sharing a `concurrencyGroup`.
- `KittyClaw.Core/Automation/TriggerStateStore.cs` — persists each interval/cron automation's next scheduled fire time (`NextRunAt`) in the per-project SQLite DB (`automation_trigger_state` table). Computed once at registration and saved immediately (not recomputed from "now" on every tick), so a restart that straddles the scheduled moment still fires on time; a missed occurrence catches up with a single immediate fire on the next tick.

## Model
- **Triggers**: `interval`, `ticketInColumn`, `statusChange`, `subTicketStatus`, `ticketCommentAdded`, `gitCommit`, `boardIdle`, `agentInactivity`.
- **Conditions**: `ticketInColumn`, `ticketCountInColumn`, `fieldLength`, `priority`, `labels`, `assignedTo`, `hasParent`, `allSubTicketsInStatus`, `ticketAge`.
- **Actions**: `runAgent`, `moveTicketStatus`, `setLabels`, `assignTicket`, `addComment`, `consolidateAgentMemory`, `commitAgentMemory`, `executePowerShell`, `createTicket`.
- `{assignee}` placeholder in `runAgent.agent` / `runAgent.concurrencyGroup` resolves from the firing ticket's `assignedTo`.
- `{ticketId}` placeholder in `concurrencyGroup` and `mutuallyExclusiveWith` resolves to the firing ticket's ID, enabling per-ticket serialization while preserving parallelism across distinct tickets.
- `createTicket.title` / `createTicket.description` expand date/time placeholders: `{now}` (yyyy-MM-dd HH:mm), `{date}` (yyyy-MM-dd), `{time}` (HH:mm), `{monday}` (current week's Monday, yyyy-MM-dd), `{firstOfMonth}` (yyyy-MM-dd).
- `commitAgentMemory` detects whether `.agents/` is a standalone git repo (`.agents/.git` present) and commits there; otherwise falls back to the main workspace repo. It commits both the per-topic memory layout (`.agents/{agent}/memory/`) and any legacy flat `.agents/{agent}/memory.md`. Generated commits use the stable technical identity `{agent}@kittyclaw.local`, so `gitCommit.ignoreAuthors` can exclude the agent's whole post-run chain and avoid self-triggering loops.
- Canonical post-run chain: `runAgent` → `consolidateAgentMemory` → `commitAgentMemory`.
- **`statusChange` exactly-once delivery**: a matching transition is atomically consumed per automation before its action chain is dispatched. The persisted marker survives polling, configuration reloads, and engine restarts; a duplicate observation is suppressed with an information-level diagnostic. Distinct automations keep independent markers, and leaving then re-entering a status creates a new occurrence after the intervening status has been observed. Failed or stopped agents are not automatically replayed from the same transition; retry them through an explicit ticket transition or retry workflow.
- **Debounce stamped at chain completion**: `ITrigger.CommitFiringAsync` accepts an optional `DateTime? completedAt` parameter. `ActionExecutor` passes `DateTime.UtcNow` at the moment the entire action chain finishes (including post-run actions), so interval/cron debounce timestamps reflect chain completion time rather than emission time. Triggers that ignore `completedAt` (most non-interval ones) use their own internal timestamp unchanged.

## httpRequest action
- Outbound webhooks/integrations (ticket #137): `httpRequest` sends GET/POST/PUT/PATCH/DELETE with placeholder support (`{ticketId}`, `{ticketTitle}`, `{ticketStatus}`, `{assignee}`) in URL, body and header values.
- **Security**: http/https only; redirects disabled; SSRF guard enforced at connect time (`HttpActionClient`) — loopback/link-local (incl. cloud metadata 169.254.169.254), wildcard and multicast targets are refused unless the action sets `allowLocalTargets`; response read capped at 64 KB; logs contain only method + host + status — never the full URL (webhook tokens live in paths) nor header values.
- Runs on the detached chain path (like `executePowerShell`) so a slow endpoint cannot freeze the engine tick; `abortOnFailure` stops the remaining chain on failure/non-2xx.

## Dispatch semantics
- **Comment dedup per automation** (ticket #113): `ticketCommentAdded` persists the last consumed comment ID per automation and per ticket (`_lastCommentIdsByAutomation` in `dispatch-state.json`), written via an atomic per-key max-merge so a stale writer can never roll consumed state back. Comments dispatched by the urgent signal path are consumed in-memory so the next poll doesn't double-fire them. A first scan with no state seeds silently (no replay of the board's comment history); the legacy flat `_lastCommentIds` map seeds migrating installs.
- **Durable column-automation queue** (ticket #152): polling discovers every matching `ticketInColumn` automation and persists a snapshot of each complete action chain in FIFO order instead of running only the first match. Repeated polls deduplicate the same automation within one logical stay in a column; leaving and re-entering the column creates a new occurrence. Queue claims use a 35-minute lease, so an interrupted running entry is recovered without changing FIFO order.
- **Execution-time validation**: before running a queued snapshot, the processor verifies that the automation still exists and is enabled, the ticket still exists and remains in a watched column, the assignee filter still matches, and all conditions still pass. It records `Completed`, `Skipped`, `Failed`, or `Cancelled` together with a reason where applicable, then advances the queue.
- **Column-loop protection**: when the same ticket repeats the same previous-column-to-current-column transition within ten minutes, entries for the repeated occurrence are created as `Cancelled` rather than executed. Multiple automations for one occurrence and ordinary progress through distinct columns are not treated as loops.
- **Bounded level-trigger retries**: when a queued `ticketInColumn` chain ends with `runAgent` and the agent neither changes the ticket status nor receives a new non-automation comment, the same queue entry becomes pending again instead of creating a new occurrence. Successful no-progress runs wait at least the trigger's polling interval; failed runs use durable exponential backoff from `failureBackoffSeconds` (default 60 seconds) up to `maxFailureBackoffSeconds` (default 1,800 seconds). A new non-automation comment resets the attempt counter, while a status change completes the occurrence. A manually stopped run is cancelled without retry.
- **Consecutive-run cap**: `maxConsecutiveRuns` defaults to 5 and is clamped to at least one. On reaching the cap without relevant input or status progress, the processor moves the ticket to `Blocked`, adds one idempotent `[automation-retry-cap]` diagnostic comment, and cancels the queue entry. Chains with actions after `runAgent` retain their normal completion semantics and are not level-retried by this mechanism.
- **Missing-agent-definition warning** (ticket kittyclaw-front#211): a board member can exist without an agent definition on disk, so a ticket assigned to it never dispatches (typically filtered by an `assignedTo` allowlist condition) and used to freeze with no trace. For every `ticketInColumn` firing whose automation runs the `{assignee}` agent, `MissingAgentDefinitionWarner` probes `.agents/{assignee}/SKILL.md` before condition evaluation; when missing it appends a `SKIPPED #<id>: no agent definition for '<slug>'` line to `.agents/channel/debug.log` (throttled to once per hour per ticket+assignee) and posts a one-time `automation` activity on the ticket so the gap is visible from the board UI.
- [Scheduled tickets](./ticket-scheduling.md) raise the regular ticket-status signal when promoted, so status-based automations react as they do to a manual move.

## Observability
- `GET /api/engine/health` (ticket #114) — anti-silent-outage endpoint. Engine-level: `startedAt`, `lastTickAt`, `lastTickAgeSeconds` (a stale value means the tick loop is dead or hung). Per project: `loaded`, automation counts, `scheduledRegistered` (cron/interval triggers actually registered), `nextRunAt`, `overdue` (scheduled tasks sitting >2 min past their fire time), and `lastFiredAt`/`lastFiredAutomationId` (in-memory, since process start).
- `GET /api/projects/{slug}/tickets/{ticketId}/automation-queue` — returns up to 100 recent queue entries for a ticket, newest first, including status, attempts, timestamps (including delayed retry availability), terminal reason, and the number of pending executable entries ahead. The board cards and ticket panel show the current occurrence's queue state.
- `ProjectRuntimeManager.ReloadProjectAsync` builds the new trigger map before swapping `Config`/`Triggers` in, so a failed reload keeps the previous coherent pair instead of leaving automations without registered triggers.

## Entry points
- Hosted at app startup via DI in `KittyClaw.Web/Program.cs`.
- Per-project configuration loaded from `<workspace>/.agents/automations.json` (seeded by the [project template](./project-template.md)).
- Configuration remains available through `<workspace>/.agents/automations.json` and the REST API; the legacy in-app editor is no longer exposed.

## External dependencies
- [Agent dispatch](./agent-dispatch.md) — the `runAgent` action launches the `claude` CLI through it.
- [Storage](./storage.md) — reads ticket/column/comment state from per-project SQLite DBs.
- `git` on PATH — the `gitCommit` trigger polls the workspace's git log.
