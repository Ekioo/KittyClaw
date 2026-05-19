# REST API

## Purpose
Exposes the project, ticket, comment, member, label, column, and automation data over HTTP so that AI agents (and the Blazor UI) can read and mutate the board programmatically.

## Key components
- `KittyClaw.Web/Api/Endpoints.cs` — `MapApiEndpoints` entry point; route definitions are split across per-domain `partial class Endpoints` files in the same folder:
  - `Endpoints.Projects.cs`, `Endpoints.Tickets.cs`, `Endpoints.Columns.cs`, `Endpoints.Labels.cs`, `Endpoints.Members.cs`, `Endpoints.Automations.cs`, `Endpoints.Runs.cs`, `Endpoints.Chat.cs`, `Endpoints.Dashboard.cs`, `Endpoints.Skills.cs`, `Endpoints.Images.cs`, `Endpoints.Browse.cs`.
- `KittyClaw.Web/Api/Contracts.cs` — request/response DTOs.
- `KittyClaw.Web/Api/OpenApiMarkdownGenerator.cs` — renders the live OpenAPI spec as human-readable Markdown; appends embedded reference guides (automations, dashboard tiles) so agents can discover the full API surface from a single `GET /api/docs`.

## Entry points
- `GET /api/docs` — Markdown documentation, generated at runtime from the OpenAPI spec. Includes: schema tables for all request/response types (e.g. `TileSidecar` with allowed `template` values), an **Automations guide**, and a **Dashboard tiles guide** (template catalogue, sidecar format, creation walkthrough).
- `GET /openapi/v1.json` — machine-readable OpenAPI JSON.
- `/api/projects/{slug}/...` — projects, tickets, comments, columns, members, labels, mentions, automations.
- `POST /api/projects/{slug}/chat/start` accepts an optional `images` array (`ChatImageDto[]`). Each DTO carries `dataUrl` (base64 data URL), `mime`, `name`, and `sizeBytes`. Server-side: MIME allow-list (JPEG, PNG, GIF, WebP), 5 MB per-image cap, 5 images per turn cap, base64 decoded and persisted to `<workspace>/.agents/channel/tmp/chat-{runId}-{i}.{ext}` before being forwarded as `ImagePaths` to `ClaudeRunContext`. Invalid images return HTTP 400 `image_rejected`.

## Conventions
- `author` is **required** on every mutating endpoint; omitting it returns HTTP 400. Use `"owner"` for the human user, plain agent name (e.g. `"programmer"`) for AI agents.
- Ticket statuses must match an existing column name in the project — fetch columns before moving tickets.
- Cross-project ticket reference syntax in comments: `#id` (same project) and `#{slug}:{id}` (other project).
- Ticket endpoints declare typed response schemas via `.Produces<T>()` and `.ProducesProblem()`. The OpenAPI spec at `/openapi/v1.json` includes full response types and error codes (400, 404) for all ticket CRUD operations. `GET /api/docs` renders these schemas with accurate example values (e.g. `"author": "owner"` is shown in every mutating request body).
- `GET /api/projects/{slug}/tickets` returns `TicketSummary[]` (a lighter projection), while individual ticket endpoints return the full `Ticket` type.

## Member deletion
- `DELETE /api/projects/{slug}/members/{memberId}` — removes a member and unassigns them from all tickets.
  - `204 No Content` — deleted successfully.
  - `404 Not Found` — member does not exist.
  - `409 Conflict` — member has slug `"owner"` and is protected; deletion is not allowed. Body: `{ "error": "cannot delete owner" }`.

## External dependencies
- [Storage](./storage.md) — reads/writes the per-project SQLite DBs.
- [Automation engine](./automation-engine.md) — many writes (status changes, comments) act as triggers.
