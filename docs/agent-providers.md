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
| GitHub Copilot | `copilot` | Install the [Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli), then run `copilot` and `/login` as the same OS user that runs KittyClaw. Override discovery with `KITTYCLAW_COPILOT_BIN`. |
| Grok Build | `grok` | Follow [KittyClaw's Grok Build guide](./grok-build.md). Override discovery with `KITTYCLAW_GROK_BIN`. |
| Mistral Vibe | `vibe` | Follow [KittyClaw's Mistral Vibe guide](./mistral-vibe.md). Override discovery with `KITTYCLAW_MISTRAL_BIN`. |
| Ollama | `ollama` | Install and start [Ollama](https://ollama.com/download), then follow the [local-model guide](./local-models.md). Ollama runs currently use Claude Code as their agent transport. |
| DeepSeek V4 | `claude` | Install Claude Code, then follow the [DeepSeek guide](./deepseek.md). DeepSeek uses its own API key from each project's vault; no Anthropic credential is used for these runs. |

## What “detected” means

KittyClaw runs the executable's version command with a short timeout. It does not read, copy, or
display account credentials. Provider authentication remains owned by the provider CLI, while
project-specific secrets such as `DEEPSEEK_API_KEY` remain write-only in the project vault.
Copilot CLI authentication uses the signed-in GitHub account and its active Copilot
subscription; KittyClaw does not store or display its credentials.

Copilot model choices use the `copilot:<model-id>` form. `copilot:default` delegates to the
model configured in Copilot CLI; the other choices follow GitHub's
[published model list](https://docs.github.com/en/copilot/reference/ai-models/supported-models).
Actual availability still depends on the user's Copilot plan and organization policies.
KittyClaw runs Copilot non-interactively with `--allow-all-tools` and `--no-ask-user`;
this bypasses per-tool approvals. KittyClaw does not pass `--allow-all-paths` or
`--allow-all-urls`.
Copilot boundary handling is observation-only; use Claude Code when a project requires
enforced boundary hooks. Silent CLI output omits usage statistics and structured tool events.

Codex and Vibe executable discovery uses synchronous five-second probes. These discard
both output streams as bytes rather than accumulating lines. Their timeout remains active
through process exit and pipe closure; on Windows the runner closes its process job before
the final drain so descendants cannot keep discovery blocked. Timeout cleanup uses a bounded
final drain of at most five seconds. The synchronous wrapper does not capture the caller's
synchronization context.

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
