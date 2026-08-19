# Local models (Ollama)

KittyClaw can dispatch agents to a local model served by Ollama instead of Anthropic's cloud API. No proxy is required: since Ollama 0.14, Ollama exposes a native Anthropic Messages API endpoint at `http://localhost:11434/v1/messages` that the `claude` CLI uses directly.

## Prerequisites

1. Install Ollama from [ollama.com](https://ollama.com) (Windows installer available).
2. Pull the model(s):
   ```
   ollama pull qwen2.5-coder:14b
   ollama pull mistral-small:24b
   ```
3. Ollama starts automatically and listens on `http://localhost:11434`.

## Configuration in KittyClaw

1. Open **Project Settings** for your project.
2. In the **Local model (Ollama)** section, enter the **Base URL** (e.g. `http://localhost:11434`).
3. Click **Discover** — KittyClaw fetches the available models from Ollama's `/api/tags` endpoint.
4. Select a default model from the dropdown and click **Save**.
5. Automation actions can reference a discovered Ollama model through their `model` field in the trusted `.agents/automations.json` file.

## Assigning a member to use an Ollama model

Set the `model` field on a `runAgent` action in the trusted `.agents/automations.json` file. Use one of the model names returned by project model discovery.

## Member default model

Each member (agent) can have a **DefaultModel** configured in the Project Settings page. When an automation action has `model: null`, the runtime resolves the agent's `DefaultModel` and uses it. This lets `{assignee}` actions dynamically pick the assignee's configured model.

## How dispatch resolves the model

At dispatch time, `ActionExecutor` resolves the model in this order:

1. If the action has an explicit `model` → use it (override).
2. If the action `model` is `null` → use the member's `DefaultModel`.
3. If the member has no `DefaultModel` → fall back to the project's `LocalModelName`.

Then it checks the effective model name (via `ModelRouting.Resolve`):

- If it starts with `claude-` → sent to the Anthropic cloud API.
- If it starts with `grok-` → dispatched to the Grok Build CLI (see [Grok Build](./grok-build.md)).
- Otherwise → treated as an Ollama model. The executor injects into the `claude` subprocess environment:

| Variable | Value |
|---|---|
| `ANTHROPIC_BASE_URL` | The configured base URL (e.g. `http://localhost:11434`) |
| `ANTHROPIC_AUTH_TOKEN` | `ollama` (required by the CLI, ignored by Ollama) |
| `ANTHROPIC_MODEL` | The selected model name (e.g. `qwen2.5-coder:14b`) |

The `--model` flag passed to the subprocess is also replaced by the model name.

## Error handling

If a non-Claude model is selected but no **Base URL** is configured, the dispatcher emits an `error` stream event and marks the run as `Failed` without launching a subprocess. The validation message is visible in the run log.

## Verifying a run

After a run completes, open the run log under `%APPDATA%/KittyClaw/runs/<run-id>/`. The `launch` event in the log will show the effective model name and the injected environment variables.

## Limitations

The Ollama Anthropic-compat layer does not support: token counting, prompt caching, batch API, image URLs, or citations. These features are not used by the `claude` CLI in agentic dispatch mode.

## Architecture note

One Ollama endpoint (base URL) is configured per project. All members in that project that use an Ollama model share the same base URL. Model discovery calls `{baseUrl}/api/tags` via the `GET /api/projects/{slug}/ollama-models` endpoint.
