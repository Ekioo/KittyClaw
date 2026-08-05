# Getting started with KittyClaw

This guide starts after installation and walks through a first project, from creating the workspace to reviewing an agent's work.

## First launch

Open **http://localhost:5230**. On the first visit, KittyClaw checks Git and every supported agent provider: Claude Code, OpenAI Codex CLI, Grok Build, Mistral Vibe, and Ollama.

You can continue when an optional provider is missing, and the board remains usable. Onboarding considers the agent requirement satisfied when at least one of Claude Code, OpenAI Codex CLI, Grok Build, or Mistral Vibe is available. Git is also required for template-driven version-control actions. Ollama is detected and its endpoint and models are configured per project after creation, but local Ollama models currently use Claude Code as their transport, so Ollama alone does not satisfy the agent requirement.

## Create your first project

1. On the home page, select **Create a project**.
2. Enter a project name and an absolute path to its workspace. This can be an existing repository or a new folder; use **Create folder** if the path does not exist yet.
3. Select **Initialize**. KittyClaw then:
   - creates the project registry entry and its board database;
   - copies the built-in skills, memory files, and `automations.json` into `<workspace>/.agents/`, plus `CLAUDE.md` into the workspace root;
   - runs `git init` if the workspace is not already a repository and Git is available;
   - creates one board member for each built-in agent role; and
   - loads the project's automations.

If template files already exist, KittyClaw asks whether to overwrite them or keep the existing versions.

KittyClaw never deletes the workspace folder. Deleting a project removes it from KittyClaw, not the files you pointed it at.

## Your first 10 minutes

Use one small, low-risk task to learn the full loop:

1. Create and initialize a project as described above, then open its full board.
2. Select `+` in the **Todo** column. Give the ticket a narrow title, describe the expected result, assign it to `programmer`, and create it.
3. The default automation moves the ticket to **InProgress** and starts the agent. Open the active-run drawer to follow its output, answer a question, steer the run, or stop it.
4. Wait for the ticket to reach **Review**. The default QA automation checks programmer tickets at this stage; read the ticket comments and run result, then inspect the work in the workspace.
5. Use the [Before Done checklist](#before-moving-a-ticket-to-done). If the result is ready, move the ticket to **Done**. If not, leave a specific comment and move it back to **Todo** for another pass.

## Choose a home view

The home page has two views:

- **Projects** shows one card per project. Open a card to reach that project's full board.
- **Kanban** shows every project as a lane, useful for scanning or moving tickets without opening each board.

The selected view is carried by the route and remembered locally. Paused projects remain visible but their automation engine is unloaded; in the unified kanban they start collapsed.

## Make KittyClaw yours

You do not have to keep the starter workflow or agent team. The easiest approach is to open the project's full board, select **New instruction**, choose **KittyClaw** as the target, and describe the result you want.

KittyClaw's project chat can inspect the live API documentation and modify the current workspace. Give it one focused change at a time, let it ask questions when necessary, and review the result in the relevant page afterward. You can copy the examples below and replace the names, stages, roles, or metrics with terms from your own project.

### Create or change board columns

> Adapt my Kanban to a publishing workflow. I want the columns Backlog, Writing, Editorial Review, Scheduled, and Published, in that order. Preserve existing tickets, choose distinct colors, and check automations that refer to old column names. Ask me before deleting anything or making an ambiguous move.

KittyClaw can create, rename, recolor, reorder, and delete columns through its API. Deleting a populated column requires choosing where its tickets move, so the prompt asks for confirmation when the intent is unclear. Automations refer to columns by name and may also need updating after a rename. The built-in **Scheduled** column has special date-based behavior; read [ticket scheduling](./ticket-scheduling.md) before replacing it.

Review the result in **Settings → Columns**.

### Create and configure a project agent

> Create a technical writer agent for this project. It should keep the English documentation aligned with repository changes without modifying product code. Let me review the setup before the agent runs for the first time.

Creating a member alone only adds an assignable board identity. A runnable custom agent also needs `<workspace>/.agents/<member-slug>/SKILL.md`, and automatic dispatch needs a `runAgent` rule that targets the same slug. Asking for the new rule to stay disabled lets you inspect it before the first run.

Review the member and default model in **Settings → Members**, then review and enable the rule in **Automations**. If the skill file is missing, a run fails instead of launching an unconfigured agent. See [project template](./project-template.md) for the workspace layout and [automation engine](./automation-engine.md) for rule configuration.

### Add a useful dashboard tile

> Add an “Open tickets by priority” donut-chart tile to the Dashboard. It should use tickets from the current project, exclude Done, show a legend, and refresh every 15 minutes. Use the local KittyClaw API and do not hard-code secrets or machine-specific URLs.

For a tile that combines reliable data collection with an interpreted summary, try:

> Create a “Repository activity” dashboard tile. Add a read-only script that safely collects useful Git activity from this workspace for the last 30 days, then add an instruction that interprets the collected data and renders the result as a clear activity trend with a short summary. Do not access the network, modify the repository, or hard-code secrets or machine-specific URLs. Let me review the collection, interpretation, and tile settings before enabling automatic refresh.

KittyClaw can create the tile's `.dashboard/<tile-slug>/` files in the current workspace. Open **Dashboard** afterward to inspect, move, resize, edit, or refresh it. The Dashboard's own **Add tile** button is also available when you prefer its specialized guided chat and review screen. For supported tile types and the file format, see [dashboard](./dashboard.md).

## Use the board

New boards have seven columns:

**Backlog → Todo → InProgress → Blocked → Scheduled → Review → Done**

- Create a ticket with the `+` button in a column header.
- Open a ticket to edit its title, description, priority, labels, assignee, status, comments, and schedule.
- Drag tickets between columns when the board is in manual sort mode.
- Use **Settings** to manage the workspace path, members, columns, labels, and model configuration.

**Scheduled** is for work that should enter another stage later. Set a date, time, and target column from the ticket panel; KittyClaw promotes the ticket when it becomes due. See [ticket scheduling](./ticket-scheduling.md).

## Before moving a ticket to Done

KittyClaw initializes these agent roles: `programmer`, `groomer`, `producer`, `qa-tester`, `committer`, `code-janitor`, `evaluator`, and `documentalist`.

An agent produces work for you to inspect; **Review** is not the same as accepted. Before moving a ticket to **Done**:

- compare the actual workspace changes with the ticket's requested outcome;
- read the agent and QA comments, including any reported test commands, results, warnings, or limitations;
- run or inspect the relevant checks yourself when the change warrants it; and
- if anything is incomplete or incorrect, add a specific comment and move the ticket back to **Todo** for another pass.

Move the ticket to **Done** only when you accept the result. In the default template, that transition can trigger the committer agent, so review first.

Runs that fail because of quota or spending limits are parked in **Blocked** to prevent a retry loop. Once the provider is usable again, move the ticket back to **Todo**.

The default provider is Claude Code. KittyClaw can also route explicitly selected models through Grok Build or OpenAI Codex CLI, and can route local models through Ollama. Each provider must be installed or configured before its models can run; see [Grok Build](./grok-build.md), [OpenAI Codex CLI](./codex-cli.md), and [local models](./local-models.md).

## Automations

Open a project and select **Automations** to manage the rules loaded from `<workspace>/.agents/automations.json`. From there you can:

- enable or disable rules;
- edit triggers, conditions, and actions;
- save changes back to the workspace;
- reload changes made directly on disk; and
- initialize the project template again, choosing whether existing files are overwritten.

Read [automation engine](./automation-engine.md) before making substantial workflow changes.

## Dashboard and ad-hoc instructions

The **Dashboard** is a free-form canvas backed by `.dashboard/` files in the workspace. Its chat can create a tile from a plain-language description, and tiles can refresh from a script, a prompt, or a schedule. See [dashboard](./dashboard.md).

On the full project board, **New instruction** opens a chat drawer. Choose an agent and send a one-off instruction; this is separate from the ticket-triggered automations above.

## Next steps

- Read [agent dispatch](./agent-dispatch.md) to understand how CLI runs are launched, streamed, steered, and stopped.
- Read [project template](./project-template.md) before customizing the seeded skills or automations.
- Use the live API documentation at **http://localhost:5230/api/docs** when integrating an external agent or tool.
