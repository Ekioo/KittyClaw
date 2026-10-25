# Workspace guide

This workspace is orchestrated by the **KittyClaw** app (a kanban that dispatches agents against tickets).

## KittyClaw API

The full and up-to-date API documentation is available at:
http://localhost:5230/api/docs

Consult it before interacting with the API. All ticket, comment, column, member, workflow and run endpoints live there.

## Agents

Automated agents for this project live under `.agents/`:

- `.agents/preamble.md` — shared context injected into every agent run.
- `.agents/skills/{skill}/SKILL.md` — project skills selected by column processors.
- `.agents/processors/column-{id}/memory/MEMORY.md` — persistent lessons for a column processor.
- `.agents/agent-budget.json` — optional daily agent spending limit.

Column processors and scheduled tasks select the model, instructions, and project skills for each stage. Follow the current execution context and use the board API above when needed.

New workspaces do not receive fixed agent roles. Existing role instructions and memories remain available in projects that already have them.
