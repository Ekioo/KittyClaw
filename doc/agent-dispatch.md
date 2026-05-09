# Agent dispatch

## Purpose
Runs an agent as a `claude` CLI subprocess, streams its stdout/stderr in near-real-time to the UI, tracks lifecycle (started, exited, killed), and persists a run record for later inspection.

## Key components
- `KittyClaw.Core/Automation/ClaudeRunner.cs` — orchestrates a single agent run.
- `KittyClaw.Core/Automation/ProcessLifecycleManager.cs` — process spawn, exit, and kill handling.
- `KittyClaw.Core/Automation/ClaudeStreamPump.cs` — pumps NDJSON events from the subprocess into the run's event list.
- `KittyClaw.Core/Automation/AgentRun.cs` — in-memory run model + event stream consumed by the UI.
- `KittyClaw.Core/Automation/SessionRegistry.cs` — tracks active sessions per agent for steering and inactivity detection.
- `KittyClaw.Core/Automation/CostTracker.cs` — records token/cost telemetry from each run.

## Entry points
- `runAgent` action from the [automation engine](./automation-engine.md).
- Ad-hoc owner prompts from the in-app new-instruction chat drawer ([Kanban UI](./kanban-ui.md)).

## External dependencies
- `claude` CLI on PATH — the actual agent runtime.
- Workspace-side `.agents/<agent>/` files (skill, memory, preamble) seeded by the [project template](./project-template.md).
- [Storage](./storage.md) — run snapshots persisted under `%APPDATA%/KittyClaw/runs/`.
