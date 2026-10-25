# MCP server

`set_ticket_parent` accepts `request.focus: true` to highlight the card in its new
parent's sub-kanban. `remove_ticket_parent` accepts optional `focus: true` to
highlight the detached card in the main kanban. Both default to false and request
focus only after success, without opening the detail panel.

`schedule_ticket` accepts `request.focus: true` (default false) to highlight the
card in **Scheduled** after successful scheduling, without opening its detail panel.

`transfer_ticket` accepts `request.focus: true` (default false) to highlight the
transferred card in the destination project's kanban after success. Connected
source and target project views navigate to the destination card. See
[REST API transfer focus](rest-api.md) for the full behavior.

`move_ticket` accepts optional `focus: true` (default false) to reveal the moved
card in its destination kanban. The `update_ticket` and `reorder_ticket` request
objects support the same option. Rejected moves do not request focus.

`create_ticket` accepts optional `focus: true` (default false) to focus the newly
created card using the same behavior as `focus_ticket` below. It still returns the
created ticket. Failed creation does not request focus.

`focus_ticket(project, id)` shares `POST /api/projects/{slug}/tickets/{id}/focus`.
It requests card focus in connected views of that project, selecting the owning
pipeline and parent kanban, revealing the card and highlighting it without opening
the detail panel. Active text filters are cleared to reveal the target. Other project
views are unaffected. Returns `ticketId`, `pipelineId`, `columnId` and a focus `url`;
the URL remains usable when no matching view is connected. Requests are transient
and do not update ticket data. Repeated requests refocus the card.

## Purpose
Exposes project management and supervision over the [Model Context Protocol](https://modelcontextprotocol.io) so any MCP client — Claude Code, Claude Desktop, or anything speaking Streamable HTTP — can drive projects and tickets without knowing the REST API. The endpoint is embedded in `KittyClaw.Web`: no separate process or secondary port. It is disabled by default.

## Connecting

```bash
# In KittyClaw, open Settings and enable the MCP server, then:
claude mcp add --transport http kittyclaw http://localhost:5230/mcp
```

The server exposes 73 tools. Reconnect clients after upgrading so they refresh their cached tool catalogue.

## Key components
- `KittyClaw.Web/Api/McpTools.cs` supplies nine board and acceptance-criteria tools, including search, filters, pipeline-aware child creation and criteria with evidence. The original seven board tools remain available.
- `KittyClaw.Web/Api/McpManagementTools.cs` supplies management and observability tools, calling REST application handlers directly. Validations, conflicts, reloads and board notifications are shared.
- `KittyClaw.Core.Tests/Api/McpManagementEndpointTests.cs` verifies schemas and workflows over isolated Streamable HTTP.
- `KittyClaw.Web/Program.cs` — registers the server (`AddMcpServer().WithHttpTransport().WithTools<McpTools>()`), maps `/mcp`, and dynamically gates requests through the persisted global setting.
- `KittyClaw.Web/Components/Pages/GlobalSettings.razor` — exposes the one-click global switch.
- Official C# SDK: [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) 2.1.0 — the SDK handles the protocol (Streamable HTTP transport, sessions, JSON-RPC, tool schemas); KittyClaw only supplies handlers.
- `server.json` (repo root) — registry metadata for [registry.modelcontextprotocol.io](https://registry.modelcontextprotocol.io), published with `mcp-publisher`.

## Core board tools

| Tool | Maps to | Notes |
|---|---|---|
| `list_projects` | `GET /api/projects` | Discover project slugs |
| `list_tickets` | `GET /api/projects/{slug}/tickets` | Optional status, assignee, search, priority, creator and parent filters |
| `get_ticket` | `GET /api/projects/{slug}/tickets/{id}` | Full ticket incl. comments, activity and acceptance criteria with evidence |
| `create_ticket` | `POST /api/projects/{slug}/tickets` | Defaults mirror REST: `Backlog`, `NiceToHave`, author `owner` |
| `comment_ticket` | `POST .../tickets/{id}/comments` | |
| `move_ticket` | `PATCH .../tickets/{id}/status` | Status must match a column name |
| `board_overview` | `GET .../columns` + `GET .../members` | Columns (in order) + valid assignees |
| `save_acceptance_criterion` | `POST .../tickets/{id}/acceptance-criteria` or `PUT .../acceptance-criteria/{criterionId}` | Omit `criterionId` to add; pass it to update one criterion. Updates replace that criterion's evidence. |
| `delete_acceptance_criterion` | `DELETE .../tickets/{id}/acceptance-criteria/{criterionId}` | Deletes one criterion and its evidence; returns a boolean |

Acceptance-criteria tools take `project`, ticket `id` and, for updates/deletions, `criterionId`. `save_acceptance_criterion` takes a typed `criterion` object with text, status (`NotEvaluated`, `PASS`, `FAIL` or `PARTIAL`) and optional evidence. Read existing criteria through `get_ticket` and include their evidence when retaining it during an update. See [ticket acceptance criteria](acceptance-criteria.md) for evidence formats and validation.

## Management and observability tools

The original seven tools keep their names and arguments. New management tools take `project` (a slug), resource IDs, and a typed `request` object for fields. The MCP schema describes nested request properties, enum values and defaults. For example:

```json
{"project":"my-project","id":42,"request":{"author":"owner","title":"Clarified scope","priority":"Required","assignedTo":"reviewer","status":"Review","expectedStatus":"InProgress"}}
```

| Area | Tools |
|---|---|
| Tickets | `update_ticket`, `schedule_ticket`, `delete_ticket`, `set_ticket_parent`, `remove_ticket_parent`, `add_ticket_dependency`, `remove_ticket_dependency`, `update_ticket_comment`, `delete_ticket_comment`, `reorder_ticket`, `transfer_ticket`, `get_ticket_evidence`, `get_ticket_brief` |
| Projects | `create_project`, `get_project`, `update_project`, `toggle_project_pause`, `configure_local_model` |
| Members | `create_member`, `update_member`, `delete_member` |
| Columns | `list_columns`, `create_column`, `update_column`, `delete_column`, `reorder_column` |
| Pipelines | `list_pipelines`, `create_pipeline`, `update_pipeline` |
| ColumnProcessors | `get_column_processor`, `save_column_processor`, `delete_column_processor` |
| ColumnExecutions | `list_column_executions`, `retry_column_execution`, `cancel_column_execution` |
| TicketQuestions | `ask_ticket_question`, `list_ticket_questions`, `answer_ticket_question` — [persistent owner questions and resumption](ticket-questions.md) |
| Runs | `list_runs`, `get_run`, `steer_run`, `stop_run`, `retry_run`, `get_run_evidence`, `list_concurrency_groups` |
| Approvals | `list_approval_requests`, `list_approval_decisions`, `list_approval_receipts`, `decide_approval`, `get_runtime_capabilities` |
| WorktreeMerges | `list_worktree_merges`, `get_worktree_merge`, `enqueue_worktree_merge`, `process_next_worktree_merge`, `resume_worktree_merge`, `retry_worktree_synchronization` |
| Labels | `list_labels`, `create_label`, `update_label`, `delete_label`, `patch_ticket_labels` |
| Skills | `list_project_skills`, `get_project_skill`, `create_project_skill`, `update_project_skill`, `delete_project_skill` |
| Engine | `get_engine_health` |

Pipeline and processor writers follow the [temporal option map](column-workflows.md#temporal-column-editor):
set column-entry requirements before selecting a mode, request agent evidence before downstream
routing/gates, and keep scheduled tasks independent. Read existing processor values before
`save_column_processor`: not every omitted field behaves as a PATCH. Explicitly set
`jevFallbackToAgent=false` on new JEV-only definitions unless an agent fallback is intended;
preserve an existing policy during unrelated edits. The option applies to uncertainty,
missing credentials, service budget failures and service errors in both JEV-only modes.

`get_run` includes buffered log events, token usage and cost. `list_runs` defaults to active runs; `includeCompleted: true` includes retained terminal runs, with optional `ticketId` and `limit` filters (1-500, default 100). Retention follows `AgentRunRegistry` and is not an archival search. Evidence remains readable through `get_run_evidence` when the run is no longer in memory.

The legacy automation tools and queue endpoints have been removed. Column processors and scheduled tasks are the workflow execution surface.

Read-only tools advertise read-only/idempotent hints. Deletions and mutations that replace state advertise destructive hints. Retry, processing, configuration and resume operations can launch agents or modify Git state; their descriptions state those effects. These hints are client guidance, not access control. Approval decisions must reflect explicit human authorization; MCP grants no additional identity or approval rights.

Secrets, raw provider hook callbacks, approval receipt fabrication, dashboard editing, pipeline-kit file transfer and project deletion are outside this tool catalogue. They are not required for the project/ticket/workflow/run management surface.


## Conventions
- Tool results are JSON with the same shape as the REST responses (camelCase, string enums) — an agent that knows `/api/docs` reads MCP payloads unchanged.
- Original board tools default `author` to `"owner"`. Management request records follow REST required fields; pass `request.author` explicitly where required.
- Shared handler HTTP errors become MCP tool errors with their status and JSON details (including transition conflicts); no-content success returns `{ "success": true, "statusCode": 204 }`.
- Unknown project slugs return a real tool error pointing at `list_projects` (the REST layer would silently show an empty board).
- Service validation errors (unknown column, empty comment, blocked-ticket saturation) surface verbatim as MCP tool errors.

## Global setting
The endpoint is unavailable by default. Open **Settings** from KittyClaw's home page and switch **Enable the MCP server** on. The persisted setting takes effect immediately: no environment variable or application restart is required. Switching it off rejects new requests and requests from already connected clients. The REST API is unaffected.

The connection URL always uses KittyClaw's effective application URL plus `/mcp`; no separate process, supervisor, or port is involved.

## Trust boundary
Identical to the REST API: KittyClaw is a self-hosted, single-machine app; both surfaces bind to localhost without authentication. Remote/multi-user auth is explicitly out of scope for v1 and would arrive together with the approval-gates roadmap if the app ever goes remote.

## Registry publication
`server.json` at the repo root follows the [official schema](https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json) (embedded-app shape: no `packages`/`remotes`; `websiteUrl` carries the setup instructions). Publish with `mcp-publisher` under the `io.github.ekioo` namespace (GitHub OAuth). **Bump `version` to match the release tag that ships the feature before publishing.**

## External dependencies
- [REST API](./rest-api.md) — the services the tools proxy; conventions (author, statuses) are shared.
- [Kanban UI](./kanban-ui.md) — board refreshes on MCP mutations via `BoardUpdateNotifier`.
