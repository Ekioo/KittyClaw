# Project template

## Purpose
Source of truth for new-project initialization. When a project is created, these files are copied into the workspace so that agents have skills, memories, and an automations config to run with.

## Key components
- `ProjectTemplate/Agents/` — per-agent folders (`SKILL.md`, a `memory/MEMORY.md` index skeleton), shared `preamble.md`, `memory-consolidation.md`, and `automations.json`. Embedded with `LogicalName` `KittyClaw.Core.AgentsTemplate/…` and written to `<workspace>/.agents/` on Initialize. Memory topic files are not shipped — the consolidation pass creates them at runtime beside the index.
- `ProjectTemplate/AGENTS.md` — the single source of truth for workspace-wide agent guidance, read natively by Codex and Grok.
- `ProjectTemplate/CLAUDE.md` — a one-line `@AGENTS.md` compatibility import for Claude Code; it contains no competing instructions.
- These root-level files are embedded with `LogicalName` `KittyClaw.Core.AgentsTemplateRoot/…` and written to the workspace root.
- `KittyClaw.Core/Services/AgentsTemplateService.cs` — enumerates the embedded resources by prefix and copies them out via `InitializeAsync(workspace, overwrite)`.

## Notes
- Source folder is `Agents/` (no leading dot) so the repo's `.agents` gitignore does not hide the template files; only the destination at runtime is `.agents/`.
- Template files must stay **generic** — no KittyClaw-specific stack references — since the same files ship to every initialized project.

## Entry points
- Project creation flow (Home page → Create → Initialize).
- After template initialization, the new-project wizard analyzes the workspace and replaces the placeholder board organization with the workflow explicitly approved by the owner. Empty workspaces receive a short requirements questionnaire first.
- **Re-initialize agent template** action on the in-app Automations page.

## External dependencies
- [Storage](./storage.md) — the workspace path is recorded in the project registry so the engine knows where the `.agents/` folder lives.
- [Automation engine](./automation-engine.md) — consumes `automations.json` from the seeded workspace.
