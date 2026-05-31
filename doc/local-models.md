# Local models (Ollama)

KittyClaw can dispatch agents to a local model served by Ollama instead of Anthropic's cloud API. No proxy is required: since Ollama 0.14, Ollama exposes a native Anthropic Messages API endpoint that the `claude` CLI uses directly.

## Prerequisites

1. Install Ollama from [ollama.com](https://ollama.com) (Windows installer available).
2. Pull the model:
   ```
   ollama pull qwen3-coder:30b
   ```
3. Ollama starts automatically and listens on `http://localhost:11434`.

## Configuration in KittyClaw

1. Open **Project Settings** for your project.
2. In the **Local model (Ollama)** section, enter:
   - **Base URL**: `http://localhost:11434` (or the address if Ollama runs on another machine)
   - **Model name**: `qwen3-coder:30b` (must match the pulled model name exactly)
3. Click **Save**.

## Assigning a member to use the local model

In the **Automations editor**, select the member action and set the **Model** dropdown to **Local (Ollama)**. The stored sentinel value is `openai-compatible`.

At dispatch time, KittyClaw injects into the `claude` subprocess environment:

| Variable | Value |
|---|---|
| `ANTHROPIC_BASE_URL` | The configured base URL |
| `ANTHROPIC_AUTH_TOKEN` | `ollama` (required by the CLI, ignored by Ollama) |
| `ANTHROPIC_MODEL` | The configured model name |

## Verifying a run

After a run completes, open the run log under `%APPDATA%/KittyClaw/runs/<run-id>/`. The `launch` event in the log will show the effective model name and environment.

## Limitations

The Ollama Anthropic-compat layer does not support: token counting, prompt caching, batch API, image URLs, or citations. These features are not used by the `claude` CLI in agentic dispatch mode.
