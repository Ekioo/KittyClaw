# KittyClaw — Agent guide

A Blazor Server + .NET 10 kanban that orchestrates agentic projects. Each project can have LLM members; a background `AutomationEngine` dispatches them through Claude Code, Grok Build, OpenAI Codex, or a local Ollama model based on triggers (column changes, intervals, git commits, …).

## Run

```
cd KittyClaw.Web && dotnet watch --non-interactive
# → http://localhost:5230
dotnet test KittyClaw.Core.Tests
```

Keep the `dotnet watch` process running — it serves the UI and the automation engine. If `dotnet build` shows MSB3027 / MSB3021 file-lock errors, they are NOT compile errors; only `error CS####` matters.

### Debug instance (isolated)

To verify changes end-to-end without touching the main instance or spending tokens, use the `kittyclaw-web-debug` launch config (`.claude/launch.json`): port **5232**, data dir `%APPDATA%/KittyClaw-debug` (own registry/projects/runs), and the mock CLI (`KittyClaw.ClaudeMock`, built on start and injected through the provider binary environment variables) so agent dispatches replay canned NDJSON scenarios instead of calling a real provider. Never verify against the main instance on :5230 — it runs the user's real projects and live agent runs. Note: `kittyclaw-web-devcheck` (:5231) shares the main data dir — its AutomationEngine dispatches real agents on real projects; prefer `kittyclaw-web-debug`.

## Repository layout

```
KittyClaw.Core/            Models, services, automation engine, embedded project template
KittyClaw.Core.Tests/      xUnit tests
KittyClaw.Web/             Blazor Server app + REST endpoints (Api/Endpoints.*.cs partials), components, CSS, JS
KittyClaw.QaRunner/        Isolated test-instance launcher (Playwright + scenario runner)
KittyClaw.ClaudeMock/      Mock agent CLI used by QaRunner for hermetic dispatch tests
ProjectTemplate/           Source of truth for new-project initialization. Embedded into
                           KittyClaw.Core.dll and copied into each workspace on Initialize.
  Agents/                    Skills, memory stubs, automations.json, preamble.md (written to <workspace>/.agents/ on Initialize).
  AGENTS.md                  Provider-neutral workspace guide (source of truth).
  CLAUDE.md                  Claude Code compatibility import of AGENTS.md.
tools/                     Repo helpers (publish-stable.ps1, …).
```

## Storage

- Project registry: `%APPDATA%/KittyClaw/registry.db` (SQLite).
- Per-project DB: `%APPDATA%/KittyClaw/projects/<slug>.db`.
- Run logs: `%APPDATA%/KittyClaw/runs/<run-id>/`.
- App settings (language, onboardingSeen): `%APPDATA%/KittyClaw/settings.json`.
- Agent memory and session state: `<workspace>/.agents/**`.

## Conventions

- **Inline SQLite migrations**: `CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN` in try/catch. No EF Migrations.
- **DTOs** are `record` types.
- **Services** are singletons injected via DI in `KittyClaw.Web/Program.cs`.
- **Blazor components**: `@rendermode InteractiveServer`, `[Parameter]`, `StateHasChanged()`. Prefer direct service calls over HTTP self-calls.
- **CSS** lives under `KittyClaw.Web/wwwroot/css/` (12 cohesive files loaded in order via `App.razor`). **JS** in `KittyClaw.Web/wwwroot/js/`.
- **English everywhere**: code comments, commit messages, ticket content, `ProjectTemplate/**`.

## Project template embedding

Files under `ProjectTemplate/` are the source of truth for new-project initialization:
- `ProjectTemplate/Agents/preamble.md`, `*/SKILL.md`, `*/memory/MEMORY.md`, `memory-consolidation.md`, `automations.json` are embedded with `LogicalName` `KittyClaw.Core.AgentsTemplate/…` and written to `<workspace>/.agents/` on Initialize. The source folder is `Agents/` (no leading dot) so the repo's `.agents` gitignore doesn't hide template files; only the destination at runtime is `.agents/`. Agent memory uses a per-topic layout: `memory/MEMORY.md` is a scored index (always injected), with one topic file per subject created at runtime (read on demand); the consolidation pass curates it.
- Everything else under `ProjectTemplate/` (notably `AGENTS.md` and its `CLAUDE.md` compatibility import) is embedded with `LogicalName` `KittyClaw.Core.AgentsTemplateRoot/…` and written to the workspace root.

`AgentsTemplateService` enumerates the embedded resources by these prefixes and copies them out via `InitializeAsync(workspace, overwrite)` (called by the project-creation flow). Keep `ProjectTemplate/**` **generic** (no KittyClaw-specific stack references) since the same files ship to every initialized project.

## Architecture docs

Per-feature architecture documentation lives under [`doc/`](doc/index.md) — start at `doc/index.md` and follow the relative links. Each feature page covers purpose, key components, entry points, and external dependencies. Each concept is explained in exactly one file.

## API

Auto-generated at runtime from the OpenAPI spec. Read it live — do not rely on any committed snapshot:

- `http://localhost:5230/api/docs` (Markdown)
- `http://localhost:5230/openapi/v1.json` (JSON)
