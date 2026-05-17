# Agent dispatch

## Purpose
Runs an agent as a `claude` CLI subprocess, streams its stdout/stderr in near-real-time to the UI, tracks lifecycle (started, exited, killed), and persists a run record for later inspection.

## Key components
- `KittyClaw.Core/Automation/ClaudeRunner.cs` — orchestrates a single agent run. Invokes `claude --print` (no `--remote-control`) and closes stdin after writing the prompt so the subprocess does not block; parallel runs across different worktrees cannot collide via IPC files.
- `KittyClaw.Core/Automation/ProcessLifecycleManager.cs` — process spawn, exit, and kill handling.
- `KittyClaw.Core/Automation/ClaudeStreamPump.cs` — pumps NDJSON events from the subprocess into the run's event list.
- `KittyClaw.Core/Automation/AgentRun.cs` — in-memory run model + event stream consumed by the UI; carries a `ChatTarget` slug so the steer endpoint knows which chat thread to append injected messages to; also holds a `PendingSteerMessages` list populated when steering arrives after stdin is closed.
- `KittyClaw.Core/Automation/SessionRegistry.cs` — tracks active sessions per agent for steering and inactivity detection.
- `KittyClaw.Core/Automation/CostTracker.cs` — records token/cost telemetry from each run.

## Entry points
- `runAgent` action from the [automation engine](./automation-engine.md).
- Ad-hoc owner prompts from the in-app new-instruction chat drawer ([Kanban UI](./kanban-ui.md)).
- `POST /api/projects/{slug}/runs/{runId}/steer` — enqueues a steering message for the active run and (when the run has a `ChatTarget`) persists it to the chat thread. Messages written while the subprocess is still running are delivered via a queued file read on the next `--resume` turn. If stdin is already closed (run has ended), `ClaudeStreamPump` calls `AgentRun.AddPendingSteerMessage` instead of silently dropping the message; the next `POST /chat/start` for the same chat target picks up those pending messages from `AgentRunRegistry`, carries them into `ClaudeRunContext.PendingSteerMessages`, and `ClaudeRunner.BuildPromptAsync` prepends them to the chat-resume prompt so the agent receives them in the following turn.

## External dependencies
- `claude` CLI on PATH — the actual agent runtime.
- Workspace-side `.agents/<agent>/` files (skill, memory, preamble) seeded by the [project template](./project-template.md).
- [Storage](./storage.md) — run snapshots persisted under `%APPDATA%/KittyClaw/runs/`.
