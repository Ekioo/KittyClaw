# Ticket acceptance criteria

Tickets expose an `acceptanceCriteria` array. Each criterion has a stable `id`, `text`,
`status` (`NotEvaluated`, `PASS`, `FAIL`, `PARTIAL`) and optional `evidence`.
The ticket panel displays these directly below the description, with independent status
selectors and an editor to add, change or remove a criterion and its proof.

Evidence contains `content`, `mediaType` and optional `fileName`. Supported media types
are `text/plain`, `application/json`, `image/png`, `image/jpeg`, `image/gif` and `image/webp`.
Image content is base64 encoded without a data-URL prefix; the UI previews it inline.
JSON is validated and displayed as text. The UI accepts images up to 5 MB; the service
limits evidence content to 8 million characters. Empty evidence can be removed in the editor.
Recorded assessments are user/agent claims, not automatically verified proof.

## REST and MCP

Read criteria and their evidence with `GET /api/projects/{slug}/tickets/{id}` or MCP `get_ticket`.
Under `/api/projects/{slug}/tickets/{id}/acceptance-criteria`:

- `POST` creates a criterion from `{ text, status?, evidence? }`.
- `PUT /{criterionId}` replaces one criterion's text, status and evidence; include existing
  evidence to retain it, or use null to clear it. Other criteria are preserved.
- `DELETE /{criterionId}` removes that criterion and its evidence.

MCP `save_acceptance_criterion(project, id, criterion, criterionId?)` creates or replaces
one criterion using the same request shape. `delete_acceptance_criterion` removes one.
Unknown tickets or criterion IDs return not-found, and invalid text/status/JSON is rejected.

## Storage and routing

`Ticket.AcceptanceCriteriaJson` stores the array in the project SQLite database.
`ProjectService.GetProjectDb` adds the default empty-array column for older projects
before any ticket queries. `TicketService` applies each mutation in a transaction and
updates `UpdatedAt`. Ticket transfers retain the complete array and embedded evidence.

Column agent prompts and JEV routing state include criteria, statuses and evidence metadata.
Text proof excerpts are capped at 4,000 characters. Binary images remain accessible through
`get_ticket`; their contents are not sent as base64 to text-only routing. Evidence hashes
ensure that even changes beyond an excerpt invalidate previous routing decisions.
Changes invalidate routing and work-review fingerprints. Structured criterion text also
joins the existing JEV acceptance checklist; recorded PASS values do not bypass its
existing evidence review and owner approval requirements.

## Processor-owned evaluation

Each processor has an `evaluateAcceptanceCriteria` option, disabled by default. It is
available in the column processor dialog, `processor.json`, REST/MCP save requests and
portable pipeline kits. Omitted values preserve the current setting in update requests;
legacy definitions and new processors default to false.

When enabled, the final agent result includes `acceptanceCriteria`, an array of
`{ id, status, evidence }` assessments referencing existing ticket criterion IDs. Only
criteria relevant to this processor's mission should be included; omitted criteria stay
unchanged. An explicit empty array means none were relevant. When a ticket has criteria,
a missing array is rejected. PASS, FAIL and PARTIAL require non-empty evidence; an
unverified criterion can be returned as NotEvaluated. The agent returns this report in
its final JSON rather than writing assessments through tools during the run.

`ColumnExecutionService.SaveAgentResultAsync` validates all assessments and commits them
atomically with the completed-agent checkpoint, before JEV observation, classic routing
or success actions. Unknown/duplicate IDs, invalid evidence and stale consumed ticket
revisions reject the attempt without partial writes. Canceled runs cannot record a report.
On retries, an existing completed-agent checkpoint is reused without reapplying assessments.
Disabled processors do not apply assessment output. Each accepted report records its
processor and execution in ticket activity.

These session assessments preserve the consumed ticket timestamp: they are session
metadata, not a new requirement or delivery. Their content/hashes still participate in
routing and review fingerprints. This preserves the existing success and owner-feedback
freshness guards while making the updated assessments visible to the routing decision.
