# Storage

## Purpose
Persists all KittyClaw data locally. Splits app-wide state from per-project data, and keeps agent runtime state inside the workspace so it travels with the repo.

## Layout

Under `%APPDATA%/KittyClaw/`:
- `registry.db` — SQLite project registry (slug, name, workspace path, …).
- `projects/{slug}.db` — per-project SQLite DB (tickets, comments, labels, columns, members).
- `uploads/` — uploaded images referenced by descriptions and comments.
- `runs/{runId}.json` — agent run snapshots (events, status, exit code).
- `settings.json` — language + onboarding flag.

Inside each workspace:
- `<workspace>/.agents/{agent}/memory.md` — per-agent memory.
- `<workspace>/.agents/{agent}/SKILL.md` — per-agent skill (seeded from the [project template](./project-template.md)).
- `<workspace>/.agents/channel/` — session state.
- `<workspace>/.agents/automations.json` — per-project automation config.
- `<workspace>/CLAUDE.md` — workspace guide for Claude.

## Key components
- `KittyClaw.Core/Data/` — EF Core contexts and inline migrations (`CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN` in try/catch).
- `KittyClaw.Core/Services/ProjectService.cs` — registry CRUD.
- `KittyClaw.Core/Services/TicketService.cs`, `ColumnService.cs`, `LabelService.cs`, `MemberService.cs`, `ChatService.cs` — per-project domain services.
- `KittyClaw.Core/Services/AppSettingsService.cs` — `settings.json` reader/writer.

## Entry points
- DI-injected singletons resolved by the [REST API](./rest-api.md), the Blazor UI, and the [automation engine](./automation-engine.md).

## External dependencies
- SQLite (via EF Core).
- The host filesystem — workspace path is owner-supplied and must remain accessible.
