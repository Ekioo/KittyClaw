# MCP server

## Purpose
Exposes the board over the [Model Context Protocol](https://modelcontextprotocol.io) so any MCP client — Claude Code, Claude Desktop, or anything speaking Streamable HTTP — can drive projects and tickets without knowing the REST API. The endpoint is embedded in `KittyClaw.Web`: no separate process or secondary port. It is disabled by default.

## Connecting

```bash
# In KittyClaw, open Settings and enable the MCP server, then:
claude mcp add --transport http kittyclaw http://localhost:5230/mcp
```

That's it — the seven tools below appear in the client's tool list.

## Key components
- `KittyClaw.Web/Api/McpTools.cs` — the v1 tool surface: seven thin proxies over the same services the REST API uses (`ProjectService`, `TicketService`, `ColumnService`, `MemberService`). Mutating tools notify `BoardUpdateNotifier` so the UI refreshes live, exactly like their REST counterparts.
- `KittyClaw.Web/Program.cs` — registers the server (`AddMcpServer().WithHttpTransport().WithTools<McpTools>()`), maps `/mcp`, and dynamically gates requests through the persisted global setting.
- `KittyClaw.Web/Components/Pages/GlobalSettings.razor` — exposes the one-click global switch.
- Official C# SDK: [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) 2.1.0 — the SDK handles the protocol (Streamable HTTP transport, sessions, JSON-RPC, tool schemas); KittyClaw only supplies handlers.
- `server.json` (repo root) — registry metadata for [registry.modelcontextprotocol.io](https://registry.modelcontextprotocol.io), published with `mcp-publisher`.

## Tools (v1)

| Tool | Maps to | Notes |
|---|---|---|
| `list_projects` | `GET /api/projects` | Discover project slugs |
| `list_tickets` | `GET /api/projects/{slug}/tickets` | Optional `status` / `assignee` filters |
| `get_ticket` | `GET /api/projects/{slug}/tickets/{id}` | Full ticket incl. comments and activity |
| `create_ticket` | `POST /api/projects/{slug}/tickets` | Defaults mirror REST: `Backlog`, `NiceToHave`, author `owner` |
| `comment_ticket` | `POST .../tickets/{id}/comments` | |
| `move_ticket` | `PATCH .../tickets/{id}/status` | Status must match a column name |
| `board_overview` | `GET .../columns` + `GET .../members` | Columns (in order) + valid assignees |

The v1 surface is deliberately frozen at these seven tools (ticket #217): no deletion, runs, automations, or agent-launch tools.

## Conventions
- Tool results are JSON with the same shape as the REST responses (camelCase, string enums) — an agent that knows `/api/docs` reads MCP payloads unchanged.
- `author` fields default to `"owner"`, like the REST endpoints. Agents should pass their own name.
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
