# Evidence decision briefs

## Purpose
Capture structured evidence from agent runs and turn it into a concise, traceable brief for a human decision. The brief distinguishes verified observations from agent claims, reports missing, stale, or contradictory evidence, and keeps acceptance, correction, and stop decisions auditable.

## Key components
- `KittyClaw.Core/Evidence/TicketEvidence.cs` and `EvidenceStore.cs` — evidence schema and durable, project-scoped evidence store persisted on disk.
- `KittyClaw.Core/Evidence/RunEvidenceCapture.cs` and `RunEvidenceAttacher.cs` — capture command, test, repository, and run observations and attach them to tickets.
- `KittyClaw.Core/Evidence/ProvenanceRules.cs` — classifies evidence trust and preserves claim provenance.
- `KittyClaw.Core/Evidence/DecisionBriefComposer.cs` and `TicketDecisionBrief.cs` — compose the canonical brief, findings, metrics, contributing run IDs, and requested decision.
- `KittyClaw.Core/Evidence/EvidenceRecoveryAdvisor.cs` — recommends recapture or reconciliation for incomplete, stale, or contradictory evidence.
- `KittyClaw.Web/Components/DecisionBriefPanel.razor` — displays the brief and records accept, correction, or stop actions in ticket history.
- `KittyClaw.Core.Tests/Evidence/EvidenceBenchmarkSuiteTests.cs` — repeatable end-to-end benchmark suite measuring evidence capture on realistic tasks across the Claude, Codex, and Grok runner paths, including crash/restart and partial-evidence scenarios.

## Entry points
- `GET /api/projects/{slug}/tickets/{id}/brief` returns the canonical brief for a ticket with captured evidence.
- Completing an agent run invokes evidence attachment for the associated ticket.
- Opening a ticket in an owner-action column renders the decision brief panel in the Blazor ticket view.

## External dependencies
- [Agent dispatch](./agent-dispatch.md) supplies completed run observations.
- [Kanban UI](./kanban-ui.md) presents the brief and decision controls.
- [REST API](./rest-api.md) exposes the canonical brief to API consumers.
- [Storage](./storage.md) provides ticket and activity data used for the decision record.
