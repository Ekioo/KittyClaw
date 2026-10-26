# Mistral Vibe

## Purpose

KittyClaw can dispatch processors, scheduled tasks, dashboard agents, and chat turns through
Mistral's Vibe CLI. Stored model ids use an explicit `mistral:` qualifier so they cannot be
confused with an Ollama model.

## Key components

- `KittyClaw.Core/Automation/MistralCli.cs` — detects the `vibe` executable and exposes the supported hosted model aliases.
- `KittyClaw.Core/Automation/MistralStreamAdapter.cs` — converts Vibe's streaming protocol into KittyClaw run events and filters replayed history when a session resumes.
- `KittyClaw.Core/Automation/AgentCliBackend.cs` — builds Vibe commands, selects trusted-workspace and automatic-approval options, and passes the selected model through `VIBE_ACTIVE_MODEL`.
- `KittyClaw.Web/Api/Endpoints.Mistral.cs` — exposes the available Mistral model catalogue to the UI.

The selectors expose Vibe's hosted aliases:

- `mistral:mistral-medium-3.5`
- `mistral:devstral-small`

KittyClaw removes the qualifier and passes the alias through `VIBE_ACTIVE_MODEL`. Vibe emits
its own session id in the streaming protocol; KittyClaw persists that id and uses `--resume`
for later turns. Vibe replays completed history when resuming, so the adapter filters entries
created before the current KittyClaw run to avoid duplicate activity.

Programmatic runs use streaming JSON, trusted-workspace mode, automatic tool approval, and the
configured maximum turn count. Mistral rates are included in KittyClaw's explicit versioned
cost card when token usage is available.

## Entry points

- Model selectors for column processors, scheduled actions, dashboard agents, and project chat.
- `GET /api/mistral-models` — returns the hosted aliases when Vibe is installed.
- Provider detection during first-launch onboarding.

## External dependencies

1. Install Vibe using the [official Mistral instructions](https://docs.mistral.ai/vibe/code/cli/install-setup).
2. Authenticate Vibe, or expose `MISTRAL_API_KEY` to the KittyClaw process.
3. Restart KittyClaw. The onboarding check and model selectors will then show **Mistral Vibe**.

KittyClaw searches for `vibe` beside the application, under its `tools` directory, in the
official uv location `~/.local/bin`, and on `PATH`. `KITTYCLAW_MISTRAL_BIN` can point to a
specific executable.

Mistral dispatch shares the session, cost, run-lifecycle, and persistence services described in
[agent dispatch](./agent-dispatch.md).
