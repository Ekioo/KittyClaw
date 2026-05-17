# REST API

## Purpose
Exposes the project, ticket, comment, member, label, column, and automation data over HTTP so that AI agents (and the Blazor UI) can read and mutate the board programmatically.

## Key components
- `KittyClaw.Web/Api/Endpoints.cs` — all `/api/...` route definitions.
- `KittyClaw.Web/Api/Contracts.cs` — request/response DTOs.
- `KittyClaw.Web/Api/OpenApiMarkdownGenerator.cs` — renders the live OpenAPI spec as human-readable Markdown.

## Entry points
- `GET /api/docs` — Markdown documentation, generated at runtime from the OpenAPI spec.
- `GET /openapi/v1.json` — machine-readable OpenAPI JSON.
- `/api/projects/{slug}/...` — projects, tickets, comments, columns, members, labels, mentions, automations.

## Conventions
- `author` is **required** on every mutating endpoint; omitting it returns HTTP 400. Use `"owner"` for the human user, plain agent name (e.g. `"programmer"`) for AI agents.
- Ticket statuses must match an existing column name in the project — fetch columns before moving tickets.
- Cross-project ticket reference syntax in comments: `#id` (same project) and `#{slug}:{id}` (other project).
- Ticket endpoints declare typed response schemas via `.Produces<T>()` and `.ProducesProblem()`. The OpenAPI spec at `/openapi/v1.json` includes full response types and error codes (400, 404) for all ticket CRUD operations. `GET /api/docs` renders these schemas with accurate example values (e.g. `"author": "owner"` is shown in every mutating request body).
- `GET /api/projects/{slug}/tickets` returns `TicketSummary[]` (a lighter projection), while individual ticket endpoints return the full `Ticket` type.

## External dependencies
- [Storage](./storage.md) — reads/writes the per-project SQLite DBs.
- [Automation engine](./automation-engine.md) — many writes (status changes, comments) act as triggers.
