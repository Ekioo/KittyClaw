# Grok Build (xAI CLI)

## Purpose
When xAI's Grok Build CLI (the `grok` binary) is installed on the host, project members can use
Grok models: dispatch runs the `grok` subprocess instead of `claude`, same idea as Claude Code.
Detection is automatic — no configuration. When the CLI is absent, the Grok model group simply
does not appear in the UI, and selecting a grok model anyway (e.g. via the API) fails the run
with an explanatory error event before any subprocess is launched.

## Key components
- `KittyClaw.Core/Automation/GrokCli.cs` — binary detection (search order: `KITTYCLAW_GROK_BIN`
  env var → sibling of the host exe → `tools/` → PATH probe, including `.cmd`/`.bat` shims on
  Windows) and model discovery: parses `grok models` output for `grok-*` ids, falling back to a
  small static list. Both are cached for the process lifetime — installing grok requires an app
  restart to be picked up.
- `KittyClaw.Core/Automation/ModelRouting.cs` — the single routing decision used by every
  dispatch site (automation actions, chat, run retry, dashboard tiles): `claude-*` or null →
  claude CLI; `grok-*` → grok CLI (error when not installed); anything else → Ollama via the
  claude CLI with `ANTHROPIC_*` env overrides (see [Local models](./local-models.md)).
- `KittyClaw.Core/Automation/ClaudeRunner.cs` — when `ClaudeRunContext.Provider == Grok`, builds
  grok headless args instead of claude's: `--output-format streaming-json --always-approve
  --no-auto-update --max-turns N`, `--session-id <uuid>` (new) / `--resume <id>` (resume),
  `--model <id>`, and the prompt as the `-p` argument (grok does not read it from stdin).
  Session keys are namespaced with a `grok:` prefix so switching a member between providers
  never tries to resume a foreign session id. The project quota-fallback model (a claude id) is
  disabled for grok runs.
- `KittyClaw.Core/Automation/GrokStreamAdapter.cs` — normalizes grok's streaming-json NDJSON
  events to the claude-style kinds the pipeline consumes: `text` → `assistant`, `tool_use`/
  `tool_call` → `tool_use`, `error` → `error`, and the terminal summary (usage/cost/stopReason)
  → `result` with token usage accumulated onto the run (feeds per-ticket cost tracking). Field
  names are matched tolerantly (snake_case and camelCase); unrecognized lines fall through to
  the generic passthrough in `ClaudeStreamPump`.
- `KittyClaw.Web/Api/Endpoints.Grok.cs` — `GET /api/grok-models`, host-global (unlike the
  per-project Ollama endpoint); returns `[]` when the CLI is not installed.

## Entry points
- Model selectors show a "Grok" optgroup when models are discovered: automation action editor,
  chat drawer, dashboard tile config, and the member default-model dropdown in Project Settings.
- Any dispatch path that resolves an effective model (action `model`, member `DefaultModel`,
  tile `model`, chat model, run retry) routes through `ModelRouting.Resolve`.

## External dependencies
- `grok` CLI installed and authenticated (`grok login`, requires a SuperGrok / X Premium+
  subscription, or `XAI_API_KEY` for API billing) — KittyClaw does not manage grok auth.
- Install: `irm https://x.ai/cli/install.ps1 | iex` (Windows) or
  `curl -fsSL https://x.ai/cli/install.sh | bash`.

## Limitations
- The prompt is passed as a command-line argument; on Windows the command line is capped at
  ~32k characters, so an extremely large skill/memory/context prompt could fail to spawn (the
  run surfaces a `spawn failed` error event).
- Grok's streaming event schema is not formally published; the adapter is tolerant but unknown
  event shapes surface as raw passthrough events in the run log rather than formatted ones.
- `AskUserQuestion` interactive widgets and claude-specific quota fallback are claude-only.
