# KittyClaw

<p align="center">
  <a href="https://www.youtube.com/watch?v=nqDHH1T5TwA">
    <img src="https://img.youtube.com/vi/nqDHH1T5TwA/maxresdefault.jpg" alt="KittyClaw demo" width="800" />
  </a>
</p>

<p align="center">
  <img src="docs/assets/demo.webp" alt="KittyClaw demo" width="800" />
</p>

<p align="center">
  <a href="https://kittyclaw.dev">kittyclaw.dev</a> · <a href="https://kittyclaw.dev/#waitlist">Get early access</a>
</p>

A kanban board that **orchestrates agentic projects**. A project can be split into independently named pipelines whose stable identities survive renames. Columns can own generic processors with persistent memory, reusable project skills, ordered ticket selection, durable retries, and switch-like routing to columns in any pipeline. Execution state is separate from business columns, so an `InProgress` column is optional. The legacy `AutomationEngine` remains available for trigger-based rules, cron/interval work, and backward compatibility. Agents run through Claude Code, OpenAI Codex, Grok Build, or a local Ollama model while their output streams into the app.

## Tech Stack

- **.NET 10** / **Blazor Server** (interactive SSR)
- **SQLite** via Entity Framework Core (one DB per project)
- **OpenAPI** with auto-generated Markdown docs
- Agent execution: at least one supported CLI — **[Claude Code CLI](https://docs.claude.com/en/docs/claude-code/overview)**, **[OpenAI Codex CLI](doc/codex-cli.md)**, or **[Grok Build](doc/grok-build.md)**. **[Ollama](https://ollama.com)** is also supported for local models through Claude Code CLI ([local-model setup](doc/local-models.md)).
- Optional for repository initialization, Git-aware automations, and agent commits: **[Git](https://git-scm.com/downloads)**

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- At least one agent CLI on your `PATH`: Claude Code (`claude`), OpenAI Codex (`codex`), or Grok Build (`grok`). Local-model execution requires both Claude Code CLI and a reachable Ollama server.
- Optional: [Git](https://git-scm.com/downloads) (`git` on your `PATH`) for repository initialization, Git-aware automations, and agent commits

On first launch, the onboarding popup checks only for Claude Code and Git; it does not probe Codex, Grok Build, or Ollama. Those checks describe the default setup path, not the runtime's provider support. You can continue without either detected tool and configure another backend. Agent runs require their selected backend to be available, while Git-dependent features require Git.

### Run

From the repo root:

```
run.bat        (Windows)
./run.sh       (macOS / Linux)
```

Both wrap `dotnet watch --project KittyClaw.Web --non-interactive` and serve the app at **http://localhost:5230** with hot reload enabled.

### Creating a project

From the home page, type a name and click **Create**. A popup asks you to set a workspace folder (absolute path to a repo/folder) and offers to create it if missing. Click **Initialize** to:

1. Create the project registry entry + per-project SQLite DB.
2. Copy the project template from `ProjectTemplate/` (`preamble.md`, `{agent}/SKILL.md`, `{agent}/memory/MEMORY.md` index, `memory-consolidation.md`, `automations.json`, `CLAUDE.md`) into the workspace — agent files under `<workspace>/.agents/`, `CLAUDE.md` at the workspace root.
3. Run `git init` if the workspace is not already a git repo (skipped if `git` isn't installed).
4. Create a member for each agent slug found in the template.
5. Navigate to the board.

The workspace folder itself is never deleted by KittyClaw, even when you delete a project.

### Data Storage

All KittyClaw data is stored locally in `%APPDATA%/KittyClaw/`:

- `registry.db` — project registry
- `projects/{slug}.db` — per-project database (tickets, comments, labels, columns, members)
- `uploads/` — uploaded images
- `runs/{runId}.json` — agent run snapshots (events, status, exit code)
- `settings.json` — language + onboarding flag

Per-project agent state lives **in the workspace**: `<workspace>/.agents/{agent}/memory/` (scored `MEMORY.md` index + per-topic lesson files), `<workspace>/.agents/channel/` (session state), etc.

## Project Structure

| Path | Description |
|---|---|
| **KittyClaw.Core** | Domain models, EF Core contexts, services, automation engine, embedded project template |
| **KittyClaw.Core.Tests** | xUnit tests (conditions, triggers, signals, JSON polymorphism) |
| **KittyClaw.Web** | Blazor Server UI + REST API |
| **KittyClaw.QaRunner** | Isolated test-instance launcher (Playwright + scenario runner) used by the qa-tester agent |
| **KittyClaw.ClaudeMock** | Mock `claude` CLI used by `KittyClaw.QaRunner` for hermetic agent dispatch in tests |
| **ProjectTemplate/** | Source of truth for new-project initialization. Files under `Agents/` are written to `<workspace>/.agents/`; `CLAUDE.md` is written to the workspace root. |
| **tools/** | Repo helpers (e.g. `publish-stable.ps1` to bundle Web + QaRunner + ClaudeMock for a stable channel) |

## Architecture

Per-feature architecture documentation lives under [`doc/`](doc/index.md). Start with [pipeline and column processing](doc/column-workflows.md) for the multi-pipeline model, or `doc/index.md` for the complete architecture map.

## API

All endpoints are under `/api`. The documentation is auto-generated from the live OpenAPI spec:

- Human-readable Markdown: `GET http://localhost:5230/api/docs`
- Machine-readable JSON: `GET http://localhost:5230/openapi/v1.json`

## For AI Agents

This app is designed to be operated by AI agents through its REST API. Here's how to get started:

1. **Read the live API docs** at `http://localhost:5230/api/docs` — every endpoint, request/response example, and schema, always up to date with the running server.
2. **Identify yourself** — `author` is **required** on every mutating endpoint; omitting it returns HTTP 400. Use your plain agent name (e.g. `"programmer"`, `"groomer"`). The human user is `"owner"`.
3. **Discover the board** — call `GET /api/projects` first, then `GET /api/projects/{slug}/columns` to learn the workflow stages and `GET /api/projects/{slug}/members` for assignable members.
4. **Use the right status** — ticket statuses must match existing column names. Fetch columns before moving tickets.
5. **Track your work** — add comments on tickets to explain what you did or what you need. Use `@mentions` to notify members, `#id` to reference tickets in the same project, and `#{slug}:{id}` to reference tickets in another project.
6. **Labels & priority** — use `GET /api/projects/{slug}/labels` to discover available labels, and set priority to `Idea`, `NiceToHave`, `Required`, or `Critical`.
7. **Check mentions** — call `GET /api/projects/{slug}/mentions/{your-handle}` to find tickets that mention you.
8. **Sub-tickets** — set `parentId` when creating a ticket to make it a child. Use `PUT /api/projects/{slug}/tickets/{id}/parent` to reparent, or `DELETE` it to detach. List sub-tickets with `?parentId={id}`.
9. **Cross-project transfers** — use `POST /api/projects/{slug}/tickets/{id}/transfer` only after checking that the target project has compatible columns, assignees, and labels. The operation preserves the ticket tree and its history or rejects the transfer without changing either project. See [Lossless ticket transfer](doc/ticket-transfer.md).

## Conventions

- **Author format**: `"owner"` for the human user, plain agent name (e.g. `"programmer"`) for AI agents
- **Priority levels**: `Idea`, `NiceToHave`, `Required`, `Critical`
- **Default column**: `Backlog`

## UI Features

- Onboarding popup on first launch that checks the default Claude Code + Git setup (other supported backends are configured separately)
- Project creation popup with workspace selection + one-click agent template initialization
- Unified multi-project home with project cards and kanban swimlanes
- Kanban board with drag-and-drop
- Customizable dashboard view with free-drag tiles (Markdown, KPI, charts, Heatmap, Timeline, …), AI chat-based tile creation, and auto-refresh via LLM prompts
- Ticket detail panel with comments and activity timeline
- Live agent run drawer (SSE stream of provider output, steer + stop controls)
- New-instruction chat drawer to send an ad-hoc prompt to an agent
- Automations page: list, enable/disable, edit (triggers / conditions / actions), reload from disk, re-initialize agent template
- Markdown rendering with `@mention`, `#id`, and `#{slug}:{id}` cross-project ticket reference support
- Advanced search syntax: `#42`, `@owner`, `>date`, `priority:critical`, `label:bug`, `by:owner`
- Sub-tickets with parent/child relationships and progress tracking
- Lossless, atomic ticket-tree transfers between projects through the REST API
- Column management (create, reorder, customize colors)
- Label and member management
- Image upload in descriptions and comments
- Local model support ([Ollama](doc/local-models.md)): per-project base URL with model autodiscovery, per-member default model, per-action override in the Automation Editor
- Provider-aware dispatch through Claude Code, [OpenAI Codex](doc/codex-cli.md), [Grok Build](doc/grok-build.md), or Ollama, with conversation handoff and unavailable-model fallback

## Dashboard

Each project has a customizable **Dashboard** view alongside the kanban board. Tiles are free-dragged, auto-refresh on a schedule, and can be created or edited from the in-app AI chat panel — the agent writes the tile's folder for you.

<p align="center">
  <img src="docs/assets/dashboard.png" alt="KittyClaw dashboard" width="800" />
</p>

### Tile types

| Template id   | What it renders                                                       |
| ------------- | --------------------------------------------------------------------- |
| `markdown`    | Free-form Markdown content                                            |
| `table`       | Tabular data with headers and rows                                    |
| `kpi`         | Single large number with label and optional delta                     |
| `kpi-grid`    | Grid of multiple KPI cards                                            |
| `progress`    | Progress bar with current / target values                             |
| `sparkline`   | Compact inline trend line                                             |
| `bar-chart`   | Vertical or horizontal bar chart                                      |
| `donut`       | Donut / pie chart of categorical proportions                          |
| `gauge`       | Radial gauge for a bounded value                                      |
| `status-grid` | Grid of colored status pills (up/down/warn)                           |
| `heatmap`     | Calendar-style heatmap of intensity over time                         |
| `leaderboard` | Ranked list with scores                                               |
| `timeline`    | Chronological list of events                                          |
| `image`       | Static or refreshed image                                             |
| `mermaid`     | Mermaid diagram (flowchart, sequence, …)                              |

### Folder layout

Each tile lives in its own folder under `.dashboard/` in the project workspace:

```
.dashboard/
  <tile-slug>/
    tile.yaml        # template, title, refresh schedule, prompt
    script.ps1       # optional refresh script (or script.sh, script.py, …)
    output.json      # last refresh output consumed by the template
```

### `tile.yaml` key fields

- `template` — one of the ids in the table above.
- `title` — display name shown in the tile header.
- `refresh` — interval (e.g. `5m`, `1h`) for periodic refresh.
- `refreshAt` — cron-style time-of-day refresh (alternative to `refresh`).
- `prompt` — instructions sent to the agent when (re)generating `output.json`.

Tiles can be created from the dashboard's AI chat panel by describing what you want — the agent picks a template, writes `tile.yaml`, generates the refresh script, and produces the initial `output.json`.

## Automation model

- **Triggers**: `interval`, `ticketInColumn`, `statusChange`, `subTicketStatus`, `ticketCommentAdded`, `gitCommit`, `boardIdle`, `agentInactivity`.
- **Conditions**: `ticketInColumn`, `ticketCountInColumn`, `fieldLength`, `priority`, `labels`, `assignedTo`, `hasParent`, `allSubTicketsInStatus`, `ticketAge`.
- **Actions**: `runAgent`, `moveTicketStatus`, `setLabels`, `assignTicket`, `addComment`, `consolidateAgentMemory`, `commitAgentMemory`, `executePowerShell`, `createTicket`, `httpRequest` (outbound webhooks; loopback/link-local targets blocked unless `allowLocalTargets`).
- `{assignee}` placeholder in `runAgent.agent` / `runAgent.concurrencyGroup` resolves from the firing ticket's `assignedTo`.
- Canonical post-run chain: `runAgent` → `consolidateAgentMemory` (focused claude pass that curates the agent's `memory/` index + topic files) → `commitAgentMemory` (commits the result).

## Telemetry

KittyClaw sends **one anonymous heartbeat per day** to a self-hosted-friendly analytics service ([Umami](https://umami.is)) so we know how many instances are alive and which versions run in the wild. The payload contains exactly three fields and nothing else:

- a random instance id (a GUID generated locally on first run — not tied to any user, machine, or project data)
- the KittyClaw version
- the OS family (`Windows` / `macOS` / `Linux`)

No ticket content, project names, hostnames, or usage details are ever sent. Failures are silent and never affect the app. Development instances never send telemetry.

---

## License

KittyClaw is licensed under the **[AGPL-3.0-or-later](LICENSE)**. Self-hosting and personal use are unrestricted; if you distribute a modified version or offer one as a network service, you must publish your source under the same license.

Additional terms under AGPL §7 (full text in [NOTICE.md](NOTICE.md)): derivative works must **keep the KittyClaw attribution visible** (the in-app legal notice and a "based on KittyClaw" statement in their README), must **not misrepresent their origin**, and receive **no rights to the KittyClaw name or logos**.

Two things the AGPL does **not** touch (see [NOTICE.md](NOTICE.md)):

- **Your projects**: the template files KittyClaw copies into your workspace (`.agents/`, `CLAUDE.md`, …) are additionally MIT-licensed, and everything the app produces for you (tickets, logs, agent commits, …) is yours, license-free. Managing a project with KittyClaw never places that project under the AGPL.
- **The past**: versions up to and including v0.11 were released under MIT and remain available under those terms.

---

## More Projects & Contact

→ **Site + demo:** [kittyclaw.dev](https://kittyclaw.dev)

Check out my other projects at **[ekioo.com](https://ekioo.com)**.

Follow me on X: **[@DamienHOFFSCHIR](https://x.com/DamienHOFFSCHIR)**
