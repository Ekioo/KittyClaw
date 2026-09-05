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
- `GET /api/projects/{slug}/chat/model?target={target}` — returns the model bound to an existing chat conversation. For legacy conversations with history but no stored binding, it returns the model from the last completed run; otherwise it returns `null`.
- `POST /api/projects/{slug}/tickets/{id}/transfer` — atomically transfers a ticket tree to another project while preserving identifiers and history; see [Lossless ticket transfer](./ticket-transfer.md).
- `POST /api/projects/{slug}/tickets` — enforces the project-wide Blocked-ticket limit before persistence. At saturation it returns HTTP 409 with `code: "blocked_ticket_limit_reached"`, the observed count, configured limit, and matching column IDs. An explicit `saturationOverride` is accepted only for owner-created recovery work with a non-empty audit reason.
- `GET /api/projects/{slug}/tickets/{id}/brief` — returns the canonical, provenance-preserving [evidence decision brief](./evidence-decision-briefs.md); returns `404` when the ticket does not exist or has no captured evidence.
- `POST /api/projects/{slug}/tickets/{id}/dependencies` — adds a directed "blocked-by" edge (body `{ "blockedById": <ticketId> }`) inside a serializable transaction so concurrent writes cannot produce a partial or cyclic graph. Returns `201` with the created edge, or `422` with a machine-readable `reason` (`self_reference`, `missing_ticket`, `incompatible_state`, `duplicate_edge`, `cycle`) when validation rejects it; malformed bodies return `400`. Cross-project references are rejected as `missing_ticket`.
- `DELETE /api/projects/{slug}/tickets/{id}/dependencies/{depId}` — atomically removes an edge. `204` on success; otherwise a structured `404` body with `reason: "dependency_not_found"`.
- `GET /api/projects/{slug}/tickets/{id}` exposes both dependency directions as `blockedBy` and `blocks` (each entry: dependency id, ticket id, title, status); the [kanban UI](./kanban-ui.md) renders them in the ticket panel.
- `GET /api/projects/{slug}/git` — Git status of the project's configured workspace (`GitRepositoryStatus`: workspace configured/exists, git availability, `.git` metadata detection, resolved repository root and current branch). `404` if the project does not exist.
- `POST /api/projects/{slug}/git/init` — runs `git init` strictly inside the configured workspace (no client-supplied path); see [per-ticket worktree workflow](./worktree-workflow.md#initializing-a-missing-repository). Returns `200` on creation, `409` if Git metadata already exists (directory or file `.git`), `400` for a missing/unconfigured workspace or unavailable git.
- `POST /api/projects/{slug}/chat/start` accepts an optional `images` array (`ChatImageDto[]`). Each DTO carries `dataUrl` (base64 data URL), `mime`, `name`, and `sizeBytes`. Server-side: MIME allow-list (JPEG, PNG, GIF, WebP), 5 MB per-image cap, 5 images per turn cap, base64 decoded and persisted to `<workspace>/.agents/channel/tmp/chat-{runId}-{i}.{ext}` before being forwarded as `ImagePaths` to `AgentRunContext`. Invalid images return HTTP 400 `image_rejected`.

## Conventions
- `author` is **required** on every mutating endpoint; omitting it returns HTTP 400. Use `"owner"` for the human user, plain agent name (e.g. `"programmer"`) for AI agents.
- Ticket statuses must match an existing column name in the project — fetch columns before moving tickets.
- Cross-project ticket reference syntax in comments: `#id` (same project) and `#{slug}:{id}` (other project).
- Ticket endpoints declare typed response schemas via `.Produces<T>()` and `.ProducesProblem()`. The OpenAPI spec at `/openapi/v1.json` includes full response types and error codes (400, 404) for all ticket CRUD operations. `GET /api/docs` renders these schemas with accurate example values (e.g. `"author": "owner"` is shown in every mutating request body).
- `GET /api/projects/{slug}/tickets` returns `TicketSummary[]` (a lighter projection), while individual ticket endpoints return the full `Ticket` type.
- Cross-project transfers preflight identifiers and project-specific mappings. A conflict returns a validation error without mutating either database.
- Blocked-ticket saturation is configured by `blockedTicketLimit` in `.agents/automations.json` (default `7`; `0` disables the guard). The count spans every pipeline, uses the semantic `Blocked` column role, and retains compatibility with legacy columns named exactly `Blocked`; other waiting columns do not count.

## Member deletion
- `DELETE /api/projects/{slug}/members/{memberId}` — removes a member and unassigns them from all tickets.
  - `204 No Content` — deleted successfully.
  - `404 Not Found` — member does not exist.
  - `409 Conflict` — member has slug `"owner"` and is protected; deletion is not allowed. Body: `{ "error": "cannot delete owner" }`.

## External dependencies
- [Storage](./storage.md) — reads/writes the per-project SQLite DBs.
- [Automation engine](./automation-engine.md) — many writes (status changes, comments) act as triggers.
