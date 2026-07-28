# Automation engine

## Purpose
Background service that watches each project for events and dispatches agents in response. Drives the agentic workflow: when a ticket moves, a comment is posted, a commit lands, an interval elapses, etc., the engine evaluates configured automations and runs the matching actions.

## Key components
- `KittyClaw.Core/Automation/AutomationEngine.cs` — top-level wiring only; delegates to `TriggerHandler` and `RunStateManager`.
- `KittyClaw.Core/Automation/TriggerHandler.cs` — owns the tick loop (urgent drain + per-project poll).
- `KittyClaw.Core/Automation/RunStateManager.cs` — encapsulates the 5 dispatch-gate checks (`ShouldSkipAsync`); shared by `AutomationEngine` and `ActionExecutor`.
- `KittyClaw.Core/Automation/ActionExecutor.cs` — condition evaluation and all `Execute*Async` action implementations; delegates skip checks to `RunStateManager`. Holds `_inFlightChains` (`ConcurrentDictionary` keyed by `"{automationId}:{ticketId}"`) to serialize action chains per (automation, ticket) pair — a second firing is dropped while one is already in progress.
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
- `commitAgentMemory` detects whether `.agents/` is a standalone git repo (`.agents/.git` present) and commits there; otherwise falls back to the main workspace repo. It commits both the per-topic memory layout (`.agents/{agent}/memory/`) and any legacy flat `.agents/{agent}/memory.md`.
- Canonical post-run chain: `runAgent` → `consolidateAgentMemory` → `commitAgentMemory`.
- **`statusChange` trigger re-fire on failure**: for `runAgent` actions backed by a `statusChange` (or `subTicketStatus`) trigger, `ActionExecutor` defers advancing the trigger snapshot (`commitAsync`) until after the run completes. If the run ends with `AgentRunStatus.Failed` or `Stopped`, the snapshot is left at the pre-transition value, so the next poll detects the transition again and re-dispatches the agent. On `Completed`, the snapshot advances normally and subsequent polls stay silent. This means a rate-limited or crashed agent is automatically retried on the next poll cycle (≤ `pollSeconds`) without any manual intervention.
- **Debounce stamped at chain completion**: `ITrigger.CommitFiringAsync` accepts an optional `DateTime? completedAt` parameter. `ActionExecutor` passes `DateTime.UtcNow` at the moment the entire action chain finishes (including post-run actions), so interval/cron debounce timestamps reflect chain completion time rather than emission time. Triggers that ignore `completedAt` (most non-interval ones) use their own internal timestamp unchanged.

## Entry points
- Hosted at app startup via DI in `KittyClaw.Web/Program.cs`.
- Per-project configuration loaded from `<workspace>/.agents/automations.json` (seeded by the [project template](./project-template.md)).
- Editable from the in-app **Automations** page.

## External dependencies
- [Agent dispatch](./agent-dispatch.md) — the `runAgent` action launches the `claude` CLI through it.
- [Storage](./storage.md) — reads ticket/column/comment state from per-project SQLite DBs.
- `git` on PATH — the `gitCommit` trigger polls the workspace's git log.
