# Agent dispatch

## Purpose
Runs an agent through the selected Claude Code, Grok Build, OpenAI Codex, Mistral Vibe, or Ollama-backed CLI, streams its stdout/stderr in near-real-time to the UI, tracks lifecycle (started, exited, killed), and persists a run record for later inspection.

## Key components

- `KittyClaw.Core/Automation/AgentCliBackend.cs` — provider-specific command construction, stream adaptation, and session semantics. `AgentRunner` receives one atomic `AgentDispatchTarget` instead of parallel model/provider/environment fields.
- `KittyClaw.Core/Automation/AgentRunner.cs` — orchestrates a single agent run. Invokes `claude --print` (no `--remote-control`) and closes stdin after writing the prompt so the subprocess does not block; parallel runs across different worktrees cannot collide via IPC files. Skill file reads do not pass the cancellation token, ensuring a cancelled outer scope cannot leave the run stuck in `Running` state. For contexts that enable resume recovery, a failed resume with no assistant output clears the stored session and retries with a new one. The same reset occurs when an assistant, result, stderr, error, or raw event reports a prompt/context-length limit, even if the process exits successfully or already emitted assistant output; this lets oversized resumed sessions continue from a fresh provider session. After each subprocess turn, if `AgentRun.IsAwaitingUserAnswer` is set, the runner first cancels the linked CTS and awaits `PumpSteeringAsync` so that pump task stops consuming from `SteeringQueue` (without this, pump and runner race for the same channel item and the user's answer is silently consumed by the pump, leaving the runner waiting until the 10-minute timeout); remaining queued steer messages are then drained to `PendingSteerMessages` (skipped when `IsAwaitingUserAnswer` — the runner reads the answer itself). The runner then awaits the answer from `SteeringQueue` (10-minute timeout, also cancellable via `AgentRun.Cancellation`); when the answer arrives it is added as a pending steer message and the run continues; a timeout or cancellation clears the flag and resumes without an answer. `RunAsync` creates and registers the run in `AgentRunRegistry` synchronously, before its first await: fire-and-forget callers (notably `POST /chat/start`) return the run id to the UI immediately, so the id must resolve through the registry and the SSE stream endpoint as soon as the method yields. A failure during async preparation (project lookup, worktree resolution) is pushed as an `error` event on the already-registered run and completes it as `Failed` instead of vanishing in the unobserved task.
- `KittyClaw.Core/Automation/ProcessLifecycleManager.cs` — process spawn, exit, and kill handling.
- `KittyClaw.Core/Automation/AgentStreamPump.cs` — pumps NDJSON events from the subprocess into the run's event list. The terminal `result` event's `usage` (input/output/cache token counts) and `total_cost_usd` are parsed and **accumulated** onto the `AgentRun` (`AddUsage`) — accumulated, not assigned, because one run can spawn several subprocesses (resume retry, quota fallback, steer replay), each with its own result event. When the CLI emits `{"type":"result","subtype":"error_max_turns"}`, the pump re-labels the event kind to `"max_turns"` instead of `"result"` (usage is recorded before the re-label). Tool-use events for the `AskUserQuestion` tool are emitted with kind `"ask_user_question"` (instead of `"tool_use"`) so the chat drawer can render an interactive answer widget.
- `KittyClaw.Web/Components/ChatDrawer.razor` — chat UI component. Handles `max_turns` SSE events by setting `_hitMaxTurns = true` and rendering an inline banner with a **Continue** button; clicking it pre-fills the input with "Continue" and calls `Send()`. Any sent message also clears the banner. Handles `ask_user_question` events by rendering an `AskUserQuestionMessage` widget (multiple-choice buttons or free-text textarea + Submit); the answer is posted to `POST /runs/{runId}/steer` and the widget is locked only after a successful response, while failures show a concise retryable error. `ParseAskUserQuestionDetail` extracts the prompt and options from the event's JSON input: it first tries the real CLI schema (`questions[0].question` for the prompt text and `questions[0].options[*].label` for choice labels), falling back to the legacy flat schema (`prompt` / `options` as a string array) for backward compatibility with older mocks.
- `KittyClaw.Core/Automation/AgentRun.cs` — in-memory run model + event stream consumed by the UI; carries a `ChatTarget` slug so the steer endpoint knows which chat thread to append injected messages to; also holds a `PendingSteerMessages` list populated when steering arrives after stdin is closed. `DrainPendingSteerMessages()` reads and atomically clears the list in one lock, preventing double-replay. `IsAwaitingUserAnswer` (bool) is set by `AgentStreamPump` when an `ask_user_question` event is detected and cleared by `AgentRunner` after the answer is received or the wait times out.
- `KittyClaw.Core/Automation/RunHistoryLoadingService.cs` — loads persisted run snapshots sequentially after `ApplicationStarted`, reports bounded progress, reconciles stale running snapshots before publishing them to `AgentRunRegistry`, and completes before interrupted chat recovery begins.

## Daily budget admission

`AgentBudgetService` applies the project's `dailyBudgetUsd` from the control workspace's
`.agents/agent-budget.json` to every `AgentRunner` launch: column processors, scheduled
tasks, chats, dashboard refreshes, workflow planning, memory consolidation and
manual retries. There is no chat or `ceo` exemption. A missing or non-positive limit
preserves unlimited operation; malformed or unreadable configuration fails closed.

For existing projects without the dedicated budget file, only the budget value is read from the retired `automations.json`; no rule is deserialized or executed.

The runner registers the run first, then checks before preparation, and checks again
immediately before each provider subprocess starts. Queued work, resume recovery,
fallback models and steering continuations therefore use the current configuration and
spend rather than an earlier admission decision. A denial produces a visible error and
a terminal Failed run without launching that provider attempt.

Spend uses the UTC calendar day, current and rotated cost journals (including recorded
JEV charges), and reliable known usage from current/recent runs that the asynchronous
cost recorder has not persisted yet. Already-journaled usage is not added twice; runtime
cost files resolve through the same `SessionRegistry` as the recorder, even when work
executes in a ticket or maintenance worktree.

This is an admission limit on known spend, not a monetary reservation or provider-side
hard cap. Already-running processes are not stopped; simultaneous admitted work and
usage reported only when a provider finishes can exceed the configured amount. Unknown
costs remain unpriced. The next launch/continuation is refused once known spend reaches
the cap. The global cost report remains a separate reporting view.

`AgentBudgetTests` exercises exhausted budgets across provider/scope combinations,
UTC/rotation accounting, pending recorder usage, configuration changes, project isolation,
queued work, fallback and steering replay with the mock CLI.

## CLI binary and version pinning

KittyClaw records the effective external CLI provider, binary identity, and detected version on every run and exposes it through the runs API. Detection uses `<binary> --version` with a three-second timeout; failure is recorded as a warning and never blocks dispatch. Binary overrides are `KITTYCLAW_CLAUDE_BIN`, `KITTYCLAW_CODEX_BIN`, `KITTYCLAW_GROK_BIN`, and `KITTYCLAW_MISTRAL_BIN`. Set these before starting KittyClaw to pin each provider to a specific executable. Optional `KITTYCLAW_CLAUDE_EXPECTED_VERSION`, `KITTYCLAW_CODEX_EXPECTED_VERSION`, `KITTYCLAW_GROK_EXPECTED_VERSION`, and `KITTYCLAW_MISTRAL_EXPECTED_VERSION` values add a visible non-blocking warning when the detected version differs.

## Optional RTK output optimization

Projects can explicitly opt in to [Rust Token Killer (RTK)](https://github.com/rtk-ai/rtk) from project settings. `RtkIntegrationService` probes the locally installed binary with `rtk --version` using a two-second timeout. When the probe succeeds, `AgentRunner` adds a short instruction block that prefers RTK only for supported, high-volume shell reads and requires an unchanged raw-command retry whenever exact output or missing detail matters. The provider executable, arguments, stdout protocol, and permission model are never wrapped or rewritten by KittyClaw.

The integration is disabled by default. An opted-in run always receives `RTK_TELEMETRY_DISABLED=1`; a missing, invalid, or timed-out binary produces a visible non-blocking event and otherwise leaves the run unchanged. KittyClaw never downloads RTK, runs `rtk init`, enables hooks, or edits user, repository, or agent configuration. Installation remains an explicit operator action. `KITTYCLAW_RTK_BIN` may pin the probe to a specific executable.
- `KittyClaw.Core/Automation/SessionRegistry.cs` — tracks active sessions per agent for conversation continuity in `.agents/channel/dispatch-state.json`. State updates are serialized as atomic read-modify-write operations. On transient filesystem `IOException` failures, writes are retried up to six times with exponential delays from 25 to 400 ms; non-I/O failures and exhausted retries are surfaced to the caller.
- `KittyClaw.Core/Services/ChatMemoryConsolidationService.cs` — watches ad-hoc **New instruction** conversations after their runs end. After 15 minutes without a new message (configurable with `KITTYCLAW_CHAT_MEMORY_IDLE_MINUTES`), it passes only the unconsolidated transcript segment to `.agents/memory-consolidation.md`, updates the target agent's canonical memory, and uses the existing memory commit path when files changed. The durable per-conversation checkpoint prevents replay after restart; failures use exponential retry without advancing it. Closing the drawer or changing conversations has no effect because detection is server-side. Targets without persistent memory are explicitly recorded as ignored.
- `KittyClaw.Core/Automation/CostTracker.cs` — appends per-run `CostLogEntry` lines to `<workspace>/.agents/channel/cost-log.jsonl` (rotated monthly) and answers the daily-budget check (`IsBudgetExceeded`) from that file. A provider-reported monetary total is authoritative. When Codex or Grok only reports token classes, `ModelCostEstimator` applies its explicit, versioned public rate card; unknown models remain unpriced. Estimated entries carry `CostEstimated=true`.
- `KittyClaw.Core/Automation/RunCostRecorder.cs` — hosted service subscribed to `AgentRunRegistry.OnRunEnded`. For every run that reported usage it writes the `CostLogEntry` via `CostTracker` and accumulates token/USD totals onto the ticket (`Tickets.AgentTokens` / `Tickets.AgentCostUsd` via `TicketService.AddAgentUsageAsync`) — the durable per-ticket figure shown on the board card and ticket panel, which survives the 24h run-log purge. `Tickets.AgentCostEstimated` remains true once any estimated amount contributes to that cumulative total. Run-level usage and its estimate marker also surface in the run drawer header and the `/runs` API DTOs.

## Rendered content safety

`SafeMarkdown` is shared by chat messages, ticket/comment Markdown and dashboard
Markdown. HTML and active generic attributes are disabled. Rendered link and
image URLs allow relative destinations and the `http`, `https`, `mailto` and
`tel` schemes; other schemes are replaced with `#`. This also applies to
reference links and autolinks. Disabling HTML alone does not prevent a
`javascript:` Markdown link from executing when clicked.

## Entry points

Each REST SSE subscriber has a queue of at most 256 pending events. If a client
cannot keep up, its connection is aborted so it can reconnect to the retained
run buffer; the shared agent producer and other subscribers continue. Pending
events are never silently dropped while keeping the connection open.
- `runAgent` action from the [column processing](./column-workflows.md).
- Ad-hoc owner prompts from the in-app new-instruction chat drawer ([Kanban UI](./kanban-ui.md)).
- `POST /api/projects/{slug}/runs/{runId}/steer` — enqueues a steering message for the active run and (when the run has a `ChatTarget`) persists it to the chat thread. Messages written while the subprocess is still running are delivered via a queued file read on the next `--resume` turn. If stdin is already closed (run has ended), `AgentStreamPump` calls `AgentRun.AddPendingSteerMessage` instead of silently dropping the message. On the next `POST /chat/start`, `AgentRunRegistry.LastCompletedForChatTarget()` looks up the most recent completed run for the chat target; `Endpoints.Chat.cs` calls `DrainPendingSteerMessages()` (atomic read + clear) on that run to retrieve them exactly once, carries them into `AgentRunContext.PendingSteerMessages`, and `AgentRunner.BuildPromptAsync` prepends them as steering blocks before the new user message. Additionally, when `AgentRunner` itself completes a chat run with `PendingSteerMessages.Count > 0`, it fires an **auto-continue** follow-up turn immediately (via `Task.Run`) using a cloned context — so the agent receives pending messages without requiring another user message. `PendingSteerMessages` is also serialised into the run snapshot so messages survive app restarts.

## External dependencies

- Optionally the `codex` CLI — runs explicitly qualified `codex:*` selections (see [OpenAI Codex CLI](./codex-cli.md)).
- Optionally Ollama — local model names continue through the Anthropic-compatible endpoint and are not confused with qualified Codex selections (see [Local models](./local-models.md)).
- `claude` CLI on PATH — the actual agent runtime.
- Optionally the `grok` CLI — runs the dispatch instead of claude when a `grok-*` model is selected (see [Grok Build](./grok-build.md)).
- Optionally the `vibe` CLI — runs explicitly qualified `mistral:*` selections (see [Mistral Vibe](./mistral-vibe.md)).
- Optionally the `rtk` CLI — project-scoped output optimization guidance with safe fallback; it is never installed or configured automatically.
- Workspace-side `.agents/<agent>/` files (skill, memory, preamble) seeded by the [project template](./project-template.md).
- [Storage](./storage.md) — run snapshots persisted under `%APPDATA%/KittyClaw/runs/`.

## Agent run lifecycle

`AgentRunLifecycle` owns run retention and bounded cancellation on shutdown. Project pause and explicit stop use the same cleanup helper. It does not dispatch work; `ColumnProcessingEngine`, `ColumnScheduledTaskEngine`, and ad-hoc conversations own launches.

`GET /api/engine/health` and `get_engine_health` report the column-processing service's start time, last loop heartbeat, and project pause states. Legacy automation counts, trigger scheduling, and queue entries are no longer exposed.

## Project working files folder

Project settings expose an optional absolute `WorkingFilesPath` alongside the workspace.
The registry stores it with an inline migration. `PATCH /api/projects/{slug}` accepts
`workingFilesPath`: omission preserves it, and an empty string clears it. The same path
validation as the workspace applies; existing files and control characters are rejected.
At the first provider invocation of each run, the runner resolves a base folder: the
configured path, or `<dataDir>/artifacts/<project-slug>` when the setting is empty. The
default data directory is `%APPDATA%/KittyClaw`; `KITTYCLAW_DATA_DIR` is respected for
isolated instances. Every run uses a `<run-id>` subfolder beneath that base, including
when the base is customized. The folder is created automatically; a creation failure
fails the run before launching a provider, with the path and error in the run log.
Retries, fallback providers and steering within the same run reuse the resolved folder.
New runs, including resumed conversations, get their own run folder and updated instructions.
The runner prepends instructions to use it for temporary files, scratch work, drafts,
screenshots, evidence, reports, and intermediate artifacts. This also applies to worktree
runs; deliverable source files stay in the execution workspace. Artifact directories are
retained after completion and are not included in automatic run-history cleanup.
This is an agent instruction, not a filesystem sandbox or a relocation of KittyClaw's
internal databases, logs, attachments, or agent memory.

The effective run directory is recorded as `AgentRun.WorkingFilesPath`, persisted in run
snapshots, and exposed by run detail/list responses. The execution drawer offers an
"Open working folder" button for runs with a recorded path. It opens that exact directory
in the host computer's file manager, including after restart or project setting changes;
missing directories show an error and are not recreated by this action. Older snapshots
without this field do not display the button.
