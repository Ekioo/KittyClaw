# Pipeline and column processing

## Purpose

Projects can contain multiple independent pipelines. Each pipeline and column has a stable numeric identity, so display names can be changed without breaking tickets, processors, skills, or routing rules.

This model is intended for business workflows where a ticket is claimed from a column, processed once, and routed to its next business state. It runs alongside the legacy trigger/action automation engine, which remains available for backward compatibility and cron/interval work.

## Domain model

- A project owns one or more `Pipeline` records. Renaming a pipeline changes only `Name`; its `Id` and stable `Slug` remain unchanged.
- A pipeline owns ordered `BoardColumn` records. Column names are unique only within their pipeline.
- Tickets reference both `PipelineId` and `ColumnId`. The legacy `Status` string remains synchronized with the column name.
- A column has a semantic `ColumnRole`: `Normal`, `Waiting`, `Success`, or `Failure`. Logic relies on the role, not on translated or user-editable names.
- One optional `ColumnProcessor` is attached to a column by stable `ColumnId`.

There is deliberately no required `InProgress` business column. A durable `ColumnExecution` records `Running`, `Retrying`, `WaitingForChildren`, and terminal execution states while the ticket remains in its current business column.

## Processing lifecycle

`ColumnProcessingEngine` is event-driven, with a low-frequency watchdog for delayed retries and configuration changes. For each enabled processor it:

1. claims one eligible ticket using the configured order;
2. creates a durable execution claim, protected by a unique active-ticket index;
3. dispatches the generic column agent with its mission, persistent memory, ticket context, and project skills;
4. validates the structured outcome and required-skill report;
5. selects an outcome route (switch-like rule) or the default route;
6. moves the ticket atomically to the target column, possibly in another pipeline.

At host startup, interrupted executions are recovered for every project, including projects that are paused. Recovery updates the durable execution state without dispatching new work; processing resumes only after the project is unpaused.

A route cannot point back to its source column. Repeating work is expressed through the explicit retry policy, avoiding accidental processing loops.

Technical failures use exponential backoff up to `MaxAttempts`. Once exhausted, the ticket can be routed to a dedicated technical-failure column. A failed execution can also be retried or cancelled through the API.

## Scheduled column tasks

Columns can own durable cron tasks that execute an ordered action chain without launching an agent. Each run checkpoints completed actions in the project database so a host restart resumes only the unfinished portion. If the project starts paused, interrupted runs are recovered but held in memory; they resume after the project is unpaused, and newly due tasks are not claimed while it remains paused.

`ColumnScheduledTaskService` persists definitions and run checkpoints. `ColumnScheduledTaskEngine` claims due work, recovers interrupted runs, and delegates actions to `ColumnActionExecutor`. Tasks are configured from the **Schedules** tab in the column configuration dialog.

## Generic agents and project skills

Reusable project capabilities live under:

```text
<workspace>/.agents/skills/<stable-skill-slug>/SKILL.md
```

KittyClaw writes the Codex-compatible YAML frontmatter (`name` and `description`) and keeps the editable instructions as the Markdown body. Reading an older plain-Markdown project skill upgrades it atomically before the next agent process starts.

The processor is a generic agent identity tied to a column. Its persistent memory lives under:

```text
<workspace>/.agents/processors/column-<column-id>/memory/MEMORY.md
```

The runtime prompt names this exact path and reserves current-ticket transitions for KittyClaw. Legacy specialist instructions may inform the work, but a column processor must return its outcome instead of moving or reassigning the current ticket itself.

Skills can be available, recommended, or required. Required skills must be named in the agent's structured result before business routing is accepted. Renaming a skill changes its display name while preserving its directory slug.

## Parent and child tickets

Child tickets may live in any pipeline. `BlocksParent` separates prerequisite work from informational or derived work:

- a non-blocking child never delays the parent;
- a blocking child is successful when its current column has the `Success` role;
- a parent becomes eligible only after every blocking child is successful;
- a running processor may return `wait_for_children` after creating blocking children; its durable execution resumes when they succeed.

This avoids coupling child completion to column names or to the parent's pipeline.

## User interface and API

The board displays pipeline tabs and one pipeline at a time. The **Workflows** page manages pipelines, column roles, processors, routing rules, and project skills. Its **Migrate** button opens New Instruction with an editable English migration prompt; the prompt asks for a proposal and explicit approval before changing the project. New project creation opens this page after workspace initialization.

Relevant endpoints include:

- `GET|POST /api/projects/{slug}/pipelines`
- `PATCH /api/projects/{slug}/pipelines/{pipelineId}`
- `GET|PUT|DELETE /api/projects/{slug}/columns/{columnId}/processor`
- `GET|POST /api/projects/{slug}/project-skills`
- `PATCH|DELETE /api/projects/{slug}/project-skills/{skillSlug}`
- `GET /api/projects/{slug}/column-executions`
- `POST /api/projects/{slug}/column-executions/{executionId}/retry`
- `POST /api/projects/{slug}/column-executions/{executionId}/cancel`

Ticket creation and update accept stable `pipelineId` and `columnId`. Ticket update also accepts `blocksParent`.

## Compatibility

Existing databases are migrated into a default `main` pipeline. Existing columns and tickets receive stable links, while legacy status strings and APIs continue to work. `.agents/automations.json`, polling triggers, cron/interval triggers, and legacy agent directories are not removed.
