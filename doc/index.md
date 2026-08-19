# Architecture documentation

This folder documents how KittyClaw is structured, one file per feature.
Each feature page covers its purpose, key components, entry points, and external dependencies.
Concepts are explained in exactly one place — other pages cross-link via relative links.

For a high-level overview see the root [README.md](../README.md). For repo layout and conventions see [AGENTS.md](../AGENTS.md).

## Features

- [Repository data policy](./repository-data-policy.md) — prevents execution evidence and private exports from entering published history.

- [Onboarding](./onboarding.md) — cross-platform workspace selection, guided workflow design, and the first processor-driven ticket.
- [OpenAI Codex CLI](./codex-cli.md) — dispatching explicitly qualified `codex:*` models to the `codex` CLI.
- [Mistral Vibe](./mistral-vibe.md) — dispatching explicitly qualified `mistral:*` models to the `vibe` CLI.
- [Agent provider CLIs](./agent-providers.md) — installation, detection, authentication ownership, and executable overrides for every supported provider.
- [DeepSeek V4](./deepseek.md) — project-vault authentication and dispatch through DeepSeek's Anthropic-compatible API.
- [Automation engine](./automation-engine.md) — triggers, conditions, and actions that dispatch agents.
- [Pipeline and column processing](./column-workflows.md) — stable multi-pipeline workflows, generic column agents, routing, retries, project skills, and child-ticket completion.
- [Pipeline kits](./pipeline-kits.md) — sanitized export plus write-free analysis and atomic installation of untrusted portable pipeline archives.
- [Agent dispatch](./agent-dispatch.md) — running the `claude` CLI as a subprocess and streaming its output.
- [Temporary approvals](./temporary-approvals.md) — traceable, expiring approval requests that pause and resume provider runs at the process boundary.
- [Runtime boundary enforcement](./runtime-boundary-enforcement.md) — provider-native pre-effect enforcement, fail-closed dispatch, and explicit runtime exclusions.
- [Project template](./project-template.md) — embedded `ProjectTemplate/` files copied into each workspace on Initialize.
- [REST API](./rest-api.md) — OpenAPI-driven endpoints under `/api`, with auto-generated Markdown docs.
- [MCP server](./mcp.md) — embedded Streamable-HTTP endpoint at `/mcp`; seven board tools for any MCP client.
- [Storage](./storage.md) — SQLite registry, per-project DBs, run logs, and workspace-side agent state.
- [Project secrets vault](./project-secrets.md) — per-project write-only secrets, native encryption on Windows/macOS/Linux, subprocess-only injection, fail-closed behavior.
- [Kanban UI](./kanban-ui.md) — Blazor Server board, ticket panel, agent run drawer.
- [Evidence decision briefs](./evidence-decision-briefs.md) — traceable run evidence, recovery guidance, and human accept/correct/stop decisions in owner-action columns.
- [Ticket scheduling](./ticket-scheduling.md) — park tickets until a future time, then promote them into the workflow.
- [Lossless ticket transfer](./ticket-transfer.md) — atomically move a complete ticket tree and its history between projects.
- [Dashboard](./dashboard.md) — free-form tile view backed by `.dashboard/` Markdown files with drag-and-drop layout.
- [Cost reporting](./cost-reporting.md) — global agent-cost history with date, project, and pipeline filtering.
- [Graphic charter](./graphic-charter.md) — palette, typography, spacing, form controls, button variants. Reference before adding any new UI.
- [Update check](./update-check.md) — background poll of GitHub Releases that surfaces a dismissible "new version available" banner in the app shell.
- [Telemetry](./telemetry.md) — anonymous daily heartbeat to Umami Cloud (instance id, version, OS); always on outside Development.
- [Per-ticket worktree workflow](./worktree-workflow.md) — opt-in canonical root-ticket worktrees, isolated agent execution, and linear integration helpers.
- [Local models (Ollama)](./local-models.md) — dispatching agents to a local Ollama model via the Anthropic-compat endpoint.
- [Grok Build (xAI CLI)](./grok-build.md) — dispatching agents to the `grok` CLI when it is installed on the host.
