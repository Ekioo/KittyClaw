# OpenAI Codex CLI

## Purpose

KittyClaw can dispatch an agent through the OpenAI Codex CLI when it is installed on
the host. Codex models use a `codex:` qualifier in stored configuration, for example
`codex:gpt-5.6-sol`. The qualifier distinguishes them from similarly named local
Ollama models and is removed before the model name is passed to Codex.

The built-in selector deliberately follows the compact set supported by current Codex
installations: GPT-5.6 Sol, Terra and Luna, followed by GPT-5.5 and GPT-5.4. Codex does
not expose a stable non-interactive model-list command, so this catalog is versioned in
KittyClaw; a custom `codex:*` identifier can still be persisted through the API.

## Key components

- `CodexCli` detects the executable from `KITTYCLAW_CODEX_BIN`, the application
  directory, its `tools` directory, or `PATH`, and exposes the selectable models.
- `ModelRouting` resolves a `codex:*` selection to a single `AgentDispatchTarget`.
- `AgentCliBackend` owns Codex command construction and session semantics.
- `CodexStreamAdapter` converts `codex exec --json` JSONL events into KittyClaw run
  events and records the Codex thread identifier for later resume operations.
- `GET /api/codex-models` supplies model selectors only when Codex is detected.

## Execution

New turns run through `codex exec --json` with the prompt on standard input. Existing
threads use `codex exec resume <thread-id> -`. KittyClaw passes the workspace as the
process working directory and preserves the same lifecycle, streaming, fallback, and
concurrency handling used by the other CLI backends.

To use a non-standard installation, set `KITTYCLAW_CODEX_BIN` to the executable path
before starting KittyClaw. To detect accidental upgrades of that executable, set
`KITTYCLAW_CODEX_EXPECTED_VERSION` to the version reported by `codex --version`.

## Project instructions

`AGENTS.md` is the provider-neutral workspace guide used by KittyClaw projects.
`CLAUDE.md` contains only `@AGENTS.md`, preserving Claude Code compatibility without
maintaining a second competing set of instructions.
