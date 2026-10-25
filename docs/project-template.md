# Project template

## Purpose
Source of truth for shared new-project guidance and budget settings. No fixed agent roles or agent members are seeded. Column processors use their configured missions, project skills, and persistent processor memories.

## Key components
- `ProjectTemplate/Agents/` — shared `preamble.md`, `memory-consolidation.md`, and `agent-budget.json`. Embedded with `LogicalName` `KittyClaw.Core.AgentsTemplate/…` and written to `<workspace>/.agents/` on Initialize.
- `ProjectTemplate/AGENTS.md` — the single source of truth for workspace-wide agent guidance, read natively by Codex and Grok.
- `ProjectTemplate/CLAUDE.md` — a one-line `@AGENTS.md` compatibility import for Claude Code; it contains no competing instructions.
- These root-level files are embedded with `LogicalName` `KittyClaw.Core.AgentsTemplateRoot/…` and written to the workspace root.
- `KittyClaw.Core/Services/AgentsTemplateService.cs` — enumerates the embedded resources by prefix and copies them out via `InitializeAsync(workspace, overwrite)`.

## Notes
- Source folder is `Agents/` (no leading dot) so the repo's `.agents` gitignore does not hide the template files; only the destination at runtime is `.agents/`.
- Template files must stay **generic** — no KittyClaw-specific stack references — since the same files ship to every initialized project.
- Retiring the former role templates does not migrate or delete existing members, workflows, skills, or memories. Initialization only copies the current shared files; even with overwrite enabled it leaves former role directories and project-specific configuration intact.

## Entry points
- Project creation flow (Home page → Create → Initialize).
- After template initialization, the new-project wizard analyzes the workspace and replaces the placeholder board organization with the workflow explicitly approved by the owner. Empty workspaces receive a short requirements questionnaire first.
- Project initialization services and API flows that invoke `AgentsTemplateService`.

## External dependencies
- [Storage](./storage.md) — the workspace path is recorded in the project registry so the engine knows where the `.agents/` folder lives.
