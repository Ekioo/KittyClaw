# REST API

Parent association (`PUT /api/projects/{slug}/tickets/{id}/parent`) accepts
`{"parentId":123,"focus":true}` to highlight the associated card in that parent's
sub-kanban. Detachment (`DELETE /api/projects/{slug}/tickets/{id}/parent?focus=true`)
uses the optional **query parameter** and highlights the detached card in the main
kanban. Both default to false and preserve their 204 success response. Rejected
operations do not request focus; detail panels remain closed.

Scheduling (`PATCH /api/projects/{slug}/tickets/{id}/schedule`) accepts optional
`focus: true` in its request body alongside `fireAt`, `author` and `targetStatus`.
After success it highlights the card in **Scheduled**, not the eventual promotion
column, without opening the detail panel. Omitted or false leaves navigation
unchanged. Invalid scheduling and missing tickets never request focus. The response
remains the scheduled ticket.

Transfers (`POST /api/projects/{slug}/tickets/{id}/transfer`) accept optional
`focus: true`, for example `{"targetProject":"destination","actor":"owner","focus":true}`.
After a successful transfer, connected views of the source or target project focus
the transferred card in the target project's kanban. Other project views are
unaffected. Focus uses the destination pipeline and column without opening the
detail panel. The transfer response is unchanged. Omitted or false leaves navigation
unchanged; rejected transfers never request focus.

Ticket moves also accept optional `focus: true`: `PATCH .../tickets/{id}/status`,
the root `PATCH .../tickets/{id}` (including moves by `pipelineId`/`columnId`), and
`PATCH .../tickets/{id}/reorder`. For example, the status endpoint accepts
`{"status":"Todo","author":"owner","focus":true}`. Focus is requested only after
a successful operation and targets the resulting pipeline and column. Omitted or
false leaves navigation unchanged; existing response formats are preserved.

Ticket creation (`POST /api/projects/{slug}/tickets`) accepts optional `focus: true`
in the JSON body. After successful creation it requests the same kanban card focus
as the dedicated focus endpoint below. Omitted or false preserves the existing
behavior. The response remains HTTP 201 with the created ticket and its Location
header. For example: `{"title":"Analyze release notes","createdBy":"owner","status":"Backlog","focus":true}`.

`POST /api/projects/{slug}/tickets/{id}/focus` focuses the ticket card in connected
views of that project without opening its detail panel or changing ticket data.
Returns `{ ticketId, pipelineId, columnId, url }`, or 404 for a missing project/ticket.
The URL selects the ticket's pipeline and parent kanban, reveals paginated cards,
clears text filters, scrolls to and highlights the card. With no matching connected
view, callers can still open the returned URL. The MCP equivalent is `focus_ticket`.

## Purpose
Exposes the project, ticket, comment, member, label, column, and workflow data over HTTP so that AI agents (and the Blazor UI) can read and mutate the board programmatically.

## Key components
- `KittyClaw.Web/Api/Endpoints.cs` — `MapApiEndpoints` entry point; route definitions are split across per-domain `partial class Endpoints` files in the same folder:
  - `Endpoints.Projects.cs`, `Endpoints.Tickets.cs`, `Endpoints.Columns.cs`, `Endpoints.Labels.cs`, `Endpoints.Members.cs`, `Endpoints.Runs.cs`, `Endpoints.Chat.cs`, `Endpoints.Dashboard.cs`, `Endpoints.Skills.cs`, `Endpoints.Images.cs`, `Endpoints.Browse.cs`.
- `KittyClaw.Web/Api/Contracts.cs` — request/response DTOs.
- `KittyClaw.Web/Api/OpenApiMarkdownGenerator.cs` — renders the live OpenAPI spec as human-readable Markdown; appends embedded reference guides (dashboard tiles) so agents can discover the full API surface from a single `GET /api/docs`.

## Entry points
- `GET /api/docs` — Markdown documentation, generated at runtime from the OpenAPI spec. Includes: schema tables for all request/response types (e.g. `TileSidecar` with allowed `template` values), a **Dashboard tiles guide** (template catalogue, sidecar format, creation walkthrough).
- `GET /openapi/v1.json` — machine-readable OpenAPI JSON.
- `/api/projects/{slug}/...` — projects, tickets, comments, columns, members, labels, mentions, and column workflows.
- `GET /api/projects/{slug}/chat/model?target={target}` — returns the model bound to an existing chat conversation. For legacy conversations with history but no stored binding, it returns the model from the last completed run; otherwise it returns `null`.
- `POST /api/projects/{slug}/tickets/{id}/transfer` — atomically transfers a ticket tree to another project while preserving identifiers and history; see [Lossless ticket transfer](./ticket-transfer.md).
- `POST /api/projects/{slug}/tickets` — creates tickets regardless of the number of blocked tickets. An explicitly configured blocking capacity on the destination column can refuse an arrival with HTTP 400.
- `GET /api/projects/{slug}/tickets/{id}/brief` — returns the canonical, provenance-preserving [evidence decision brief](./evidence-decision-briefs.md); returns `404` when the ticket does not exist or has no captured evidence.
- `POST /api/projects/{slug}/tickets/{id}/dependencies` — adds a directed "blocked-by" edge (body `{ "blockedById": <ticketId> }`) inside a serializable transaction so concurrent writes cannot produce a partial or cyclic graph. Returns `201` with the created edge, or `422` with a machine-readable `reason` (`self_reference`, `missing_ticket`, `incompatible_state`, `duplicate_edge`, `cycle`) when validation rejects it; malformed bodies return `400`. Cross-project references are rejected as `missing_ticket`.
- `DELETE /api/projects/{slug}/tickets/{id}/dependencies/{depId}` — atomically removes an edge. `204` on success; otherwise a structured `404` body with `reason: "dependency_not_found"`.
- `GET /api/projects/{slug}/tickets/{id}` exposes both dependency directions as `blockedBy` and `blocks` (each entry: dependency id, ticket id, title, status); the [kanban UI](./kanban-ui.md) renders them in the ticket panel.
- `GET /api/projects/{slug}/git` — Git status of the project's configured workspace (`GitRepositoryStatus`: workspace configured/exists, git availability, `.git` metadata detection, resolved repository root and current branch). `404` if the project does not exist.
- `POST /api/projects/{slug}/git/init` — runs `git init` strictly inside the configured workspace (no client-supplied path); see [per-ticket worktree workflow](./worktree-workflow.md#initializing-a-missing-repository). Returns `200` on creation, `409` if Git metadata already exists (directory or file `.git`), `400` for a missing/unconfigured workspace or unavailable git.
- `POST /api/projects/{slug}/chat/start` accepts an optional `images` array (`ChatImageDto[]`). Each DTO carries `dataUrl` (base64 data URL), `mime`, `name`, and `sizeBytes`. Server-side: MIME allow-list (JPEG, PNG, GIF, WebP), 5 MB per-image cap, 5 images per turn cap, base64 decoded and persisted as temporary files before being forwarded as `ImagePaths` to `AgentRunContext`. Invalid images return HTTP 400 `image_rejected`.
  Image bytes must have a signature matching the declared MIME type; the encoded
  length is capped before base64 allocation, independently of `sizeBytes`.
  Files are stored outside the repository under the system temporary directory
  (`KittyClaw/chat-images/<runId>/`), and rejected batches remove partial files.

- Pipeline-kit request streams are bounded to 8 MiB before parsing the archive,
  including when Content-Length is absent. Oversized confirmation uploads return
  413; empty or oversized analysis bodies return 400.
- Raw dashboard outputs use a sandboxed content security policy, including SVG
  files opened directly as documents. Generated SVG scripts cannot execute in
  the application origin.

- `GET /api/projects/{slug}/runs` accepts `includeCompleted`, `ticketId`, and `limit` (1-500 when specified; MCP defaults to 100); it returns active runs by default and retained terminal runs when requested.

## Conventions
- Comment creation returns HTTP `201` with the created `Comment` and a `Location` header; invalid content returns `400`, and a missing ticket returns `404`. Clients must treat `201` as success and reconcile an uncertain write before retrying it.
- `author` is **required** on every mutating endpoint; omitting it returns HTTP 400. Use `"owner"` for the human user, plain agent name (e.g. `"programmer"`) for AI agents.
- Ticket statuses must match an existing column name in the project — fetch columns before moving tickets.
- Cross-project ticket reference syntax in comments: `#id` (same project) and `#{slug}:{id}` (other project).
- Ticket endpoints declare typed response schemas via `.Produces<T>()` and `.ProducesProblem()`. The OpenAPI spec at `/openapi/v1.json` includes full response types and error codes (400, 404) for all ticket CRUD operations. `GET /api/docs` renders these schemas with accurate example values (e.g. `"author": "owner"` is shown in every mutating request body).
- `GET /api/projects/{slug}/tickets` returns `TicketSummary[]` (a lighter projection), while individual ticket endpoints return the full `Ticket` type.
- Cross-project transfers preflight identifiers and project-specific mappings. A conflict returns a validation error without mutating either database.

## Member deletion
- `DELETE /api/projects/{slug}/members/{memberId}` — removes a member and unassigns them from all tickets.
  - `204 No Content` — deleted successfully.
  - `404 Not Found` — member does not exist.
  - `409 Conflict` — member has slug `"owner"` and is protected; deletion is not allowed. Body: `{ "error": "cannot delete owner" }`.

## External dependencies
- [Storage](./storage.md) — reads/writes the per-project SQLite DBs.

Column creation/update requests accept `maxTickets` (0 = unlimited) and `capacityBlocking` (false = warning only). Both default to disabled/non-blocking.
