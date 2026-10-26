# Ticket questions

## Purpose

Agents can request an owner decision as a durable question in the existing automation queue. The question appears on the card and below the description in ticket details, with single-choice or multiple-choice options and optional free text. This works through shared REST/MCP services for every provider; it does not depend on Claude's transient chat widget.

## Lifecycle

`ColumnExecutionService.Questions.cs` persists `TicketQuestions` using an inline SQLite migration. A question reserves the ticket's current column execution with `WaitingForAnswer` (status 6); the active-ticket unique index includes that state. An enabled processor in the current column is required, including for requests initiated outside a processor run. A supplied execution ID must belong to that ticket and its current processor. Only one unanswered question per execution is allowed; an identical repeated pending request returns the existing question.

After saving the question, the agent must stop and finish its run. The engine observes the durable status before each stage and after dispatch, bypassing result parsing failures, retries, final actions and routing for the paused run. Waiting survives host restarts without holding a subprocess open. This is an explicit structured tool call, not automatic extraction of questions from comment text or native provider question events.

Answering atomically validates the selected options, records the answer and an owner comment, updates the trigger context, clears the previous agent result, and marks the same execution `Retrying`. Completed action checkpoints remain intact. The API/UI signals the engine; normal processor scheduling, project pause, dependencies and authorization gates still apply. The resumed dispatcher receives answered questions and owner feedback in its context. The wait does not consume a failure attempt.

Repeated answers conflict. Moving the ticket or cancelling its execution closes pending questions and prevents stale resumption; disabling/removing the processor rejects answers until the workflow is repaired. Questions are workflow decisions, not grants that bypass runtime security approvals.

## Entry points

- `GET /api/projects/{slug}/tickets/{id}/questions`: full question/answer history.
- `POST /api/projects/{slug}/tickets/{id}/questions`: `{ "question": "Which scope?", "options": ["Code", "Configuration"], "multiSelect": true, "allowFreeText": true, "author": "agent", "executionId": "optional-current-execution-id" }`.
- `POST /api/projects/{slug}/tickets/{id}/questions/{questionId}/answer`: `{ "selectedOptions": ["Code"], "freeText": "Keep hooks enabled." }`. A free-text-only answer is accepted when allowed.
- MCP: `ask_ticket_question`, `list_ticket_questions`, `answer_ticket_question`. The answer tool requires an explicit human answer and may launch work.

All writes return 400 for invalid input, 404 for a missing ticket/question, or 409 for a closed question or incompatible workflow state. The OpenAPI specification generates current REST documentation.

## Components and verification

`ColumnExecutionRecovery.razor` extends the existing queue and reloads on execution notifications. `ColumnAgentDispatcher` and the shared project preamble explain the protocol to agents. `TicketQuestionTests` covers persistence, validation, stale answers, the early-answer race, and engine resume/routing. `TicketQuestionEndpointTests` round-trips actual REST and MCP transports in an isolated paused project.

See [column workflows](column-workflows.md), [agent dispatch](agent-dispatch.md), [kanban UI](kanban-ui.md), and [temporary approvals](temporary-approvals.md).
