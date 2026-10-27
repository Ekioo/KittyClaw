# Agent provider CLIs

KittyClaw dispatches work through command-line coding agents installed on the same machine as
the web application. Open **Global settings → Agent providers** to see which executables are
currently detected and to re-run the checks after an installation.

Only one cloud agent CLI is required. Install additional providers when a project needs their
models.

| Provider | Executable checked by KittyClaw | Installation and configuration |
| --- | --- | --- |
| Claude Code | `claude` | Follow the [Claude Code setup guide](https://code.claude.com/docs/en/setup), then sign in with the CLI. Override discovery with `KITTYCLAW_CLAUDE_BIN` when necessary. |
| OpenAI Codex | `codex` | Follow [KittyClaw's Codex CLI guide](./codex-cli.md). Override discovery with `KITTYCLAW_CODEX_BIN`. |
| Grok Build | `grok` | Follow [KittyClaw's Grok Build guide](./grok-build.md). Override discovery with `KITTYCLAW_GROK_BIN`. |
| Mistral Vibe | `vibe` | Follow [KittyClaw's Mistral Vibe guide](./mistral-vibe.md). Override discovery with `KITTYCLAW_MISTRAL_BIN`. |
| Ollama | `ollama` | Install and start [Ollama](https://ollama.com/download), then follow the [local-model guide](./local-models.md). Ollama runs currently use Claude Code as their agent transport. |
| DeepSeek V4 | `claude` | Install Claude Code, then follow the [DeepSeek guide](./deepseek.md). DeepSeek uses its own API key from each project's vault; no Anthropic credential is used for these runs. |

## What “detected” means

KittyClaw runs the executable's version command with a short timeout. It does not read, copy, or
display account credentials. Provider authentication remains owned by the provider CLI, while
project-specific secrets such as `DEEPSEEK_API_KEY` remain write-only in the project vault.

Restart KittyClaw, or use **Check again** in Global settings, after changing `PATH` or a
`KITTYCLAW_*_BIN` override.

## Companion tools

The same settings grid includes JEV / TypeSafe (availability and secure API-key configuration)
and RTK (local executable detection and installation guide). **Check again** refreshes both
tools as well as the provider CLIs. RTK remains opt-in in each project’s settings; its global
card only checks availability, respecting `KITTYCLAW_RTK_BIN`.

## Installation link monitoring

The `Installation links` GitHub Actions workflow checks the URLs extracted directly from
onboarding and global settings on relevant pushes and pull requests, daily at 06:17 UTC,
and on manual dispatch. Scheduled runs use the default branch. Failed runs are visible in
GitHub Actions; notifications follow each maintainer's GitHub notification settings.

Run `python -m unittest discover -s tools -p test_check_installation_links.py -v` for the
offline checker tests and `python tools/check_installation_links.py` for the live check.
The checker follows redirects, requires a successful HTTPS destination, and retries transient
network/server failures. Errors include the URL and the source components that reference it.
It checks reachability, not whether a successful page still contains the right installation
instructions; redirects are printed for review.
