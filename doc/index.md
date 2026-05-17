# Architecture documentation

This folder documents how KittyClaw is structured, one file per feature.
Each feature page covers its purpose, key components, entry points, and external dependencies.
Concepts are explained in exactly one place — other pages cross-link via relative links.

For a high-level overview see the root [README.md](../README.md). For repo layout and conventions see [CLAUDE.md](../CLAUDE.md).

## Features

- [Automation engine](./automation-engine.md) — triggers, conditions, and actions that dispatch agents.
- [Agent dispatch](./agent-dispatch.md) — running the `claude` CLI as a subprocess and streaming its output.
- [Project template](./project-template.md) — embedded `ProjectTemplate/` files copied into each workspace on Initialize.
- [REST API](./rest-api.md) — OpenAPI-driven endpoints under `/api`, with auto-generated Markdown docs.
- [Storage](./storage.md) — SQLite registry, per-project DBs, run logs, and workspace-side agent state.
- [Kanban UI](./kanban-ui.md) — Blazor Server board, ticket panel, agent run drawer.
- [Dashboard](./dashboard.md) — free-form tile view backed by `.dashboard/` Markdown files with drag-and-drop layout.
- [Graphic charter](./graphic-charter.md) — palette, typography, spacing, form controls, button variants. Reference before adding any new UI.
- [Per-ticket worktree workflow](./worktree-workflow.md) — opt-in pattern; helper scripts and `{ticketId}` placeholder ship, but agent SKILLs in `ProjectTemplate` do not use it by default.
