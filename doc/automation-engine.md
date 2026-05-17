# Automation engine

## Purpose
Background service that watches each project for events and dispatches agents in response. Drives the agentic workflow: when a ticket moves, a comment is posted, a commit lands, an interval elapses, etc., the engine evaluates configured automations and runs the matching actions.

## Key components
- `KittyClaw.Core/Automation/AutomationEngine.cs` — top-level wiring only; delegates to `TriggerHandler` and `RunStateManager`.
- `KittyClaw.Core/Automation/TriggerHandler.cs` — owns the tick loop (urgent drain + per-project poll).
- `KittyClaw.Core/Automation/RunStateManager.cs` — encapsulates the 5 dispatch-gate checks (`ShouldSkipAsync`); shared by `AutomationEngine` and `ActionExecutor`.
- `KittyClaw.Core/Automation/ActionExecutor.cs` — condition evaluation and all `Execute*Async` action implementations; delegates skip checks to `RunStateManager`.
- `KittyClaw.Core/Automation/ProjectRuntimeManager.cs` — per-project runtime dictionary and signal fan-out.
- `KittyClaw.Core/Automation/ProjectRuntime.cs` — data class holding per-project run state.
- `KittyClaw.Core/Automation/AutomationConfig.cs` — JSON-deserialized automation definitions (triggers, conditions, actions).
- `KittyClaw.Core/Automation/AutomationStore.cs` — loads/persists `automations.json` from each workspace's `.agents/` folder.
- `KittyClaw.Core/Automation/Triggers/` — trigger implementations.
- `KittyClaw.Core/Automation/GitRepositoryWatcher.cs` — backs the `gitCommit` trigger.
- `KittyClaw.Core/Automation/RunConcurrencyGate.cs` — serializes runs sharing a `concurrencyGroup`.

## Model
- **Triggers**: `interval`, `ticketInColumn`, `statusChange`, `subTicketStatus`, `ticketCommentAdded`, `gitCommit`, `boardIdle`, `agentInactivity`.
- **Conditions**: `ticketInColumn`, `ticketCountInColumn`, `fieldLength`, `priority`, `labels`, `assignedTo`, `hasParent`, `allSubTicketsInStatus`, `ticketAge`.
- **Actions**: `runAgent`, `moveTicketStatus`, `setLabels`, `assignTicket`, `addComment`, `consolidateAgentMemory`, `commitAgentMemory`, `executePowerShell`.
- `{assignee}` placeholder in `runAgent.agent` / `runAgent.concurrencyGroup` resolves from the firing ticket's `assignedTo`.
- `{ticketId}` placeholder in `concurrencyGroup` and `mutuallyExclusiveWith` resolves to the firing ticket's ID, enabling per-ticket serialization while preserving parallelism across distinct tickets.
- `commitAgentMemory` detects whether `.agents/` is a standalone git repo (`.agents/.git` present) and commits there; otherwise falls back to the main workspace repo.
- Canonical post-run chain: `runAgent` → `consolidateAgentMemory` → `commitAgentMemory`.

## Entry points
- Hosted at app startup via DI in `KittyClaw.Web/Program.cs`.
- Per-project configuration loaded from `<workspace>/.agents/automations.json` (seeded by the [project template](./project-template.md)).
- Editable from the in-app **Automations** page.

## External dependencies
- [Agent dispatch](./agent-dispatch.md) — the `runAgent` action launches the `claude` CLI through it.
- [Storage](./storage.md) — reads ticket/column/comment state from per-project SQLite DBs.
- `git` on PATH — the `gitCommit` trigger polls the workspace's git log.
