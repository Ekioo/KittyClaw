# DeepSeek V4

KittyClaw supports the current DeepSeek V4 models through DeepSeek's Anthropic-compatible API
and the Claude Code CLI. This is the integration path documented by DeepSeek and does not require
an Anthropic API key or an Anthropic account for DeepSeek runs.

## Prerequisites

1. Install [Claude Code](https://docs.anthropic.com/en/docs/claude-code/setup) so the `claude`
   executable is available to the KittyClaw process.
2. Create an API key on the [DeepSeek platform](https://platform.deepseek.com/).
3. Open the KittyClaw project, then **Project settings → Secure vault**.
4. Save the secret with the exact name `DEEPSEEK_API_KEY`.
5. Select `DeepSeek V4 Pro` or `DeepSeek V4 Flash` in a member, processor, automation, or chat
   model selector.

The secret is write-only in the UI. KittyClaw decrypts it only when starting an agent process for
that project, maps it to the authentication variable expected by Claude Code, and does not expose
it through model-discovery endpoints or run output.

## Runtime configuration

For a DeepSeek run, KittyClaw configures the child process with DeepSeek's official endpoint and
model variables:

- `ANTHROPIC_BASE_URL=https://api.deepseek.com/anthropic`
- `ANTHROPIC_MODEL` and the Claude Code default-model variables set to the selected DeepSeek V4
  model
- `ANTHROPIC_AUTH_TOKEN` set from the project's vaulted `DEEPSEEK_API_KEY`

Do not put the DeepSeek key in `.claude/settings.json`, `server.json`, the repository, or a global
environment variable. A host-level `ANTHROPIC_AUTH_TOKEN` never satisfies KittyClaw's DeepSeek
credential check.

## Models

- `deepseek:deepseek-v4-pro[1m]` — the main high-capability model.
- `deepseek:deepseek-v4-flash` — the faster, lower-cost model and sub-agent default.

KittyClaw keeps DeepSeek sessions, evidence, and cost estimates separate from Claude sessions even
though both use the `claude` executable.

See DeepSeek's [Claude Code integration guide](https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code/)
and [Anthropic-compatible API guide](https://api-docs.deepseek.com/guides/anthropic_api) for the
upstream protocol details.
