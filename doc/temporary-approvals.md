# Temporary approvals

## Purpose

Temporary approvals let an operator review a protected action with its resource, reason, scope, duration, provider, run, and ticket context. Decisions are limited to allow once, allow for the current ticket, or deny. Requests and decisions are persisted for audit, expire automatically, suppress material duplicates, and never create a global authorization by default.

## Key components

- `KittyClaw.Core/Services/ApprovalRegistryService.cs` — persists requests, decisions, receipts, expiry, duplicate suppression, and audit history.
- `KittyClaw.Core/Services/ApprovalWorkflowService.cs` — correlates persisted requests and decisions with active runs.
- `KittyClaw.Core/Automation/AgentRun.cs` — tracks the pending request and exposes the correlated wait/resume gate.
- `KittyClaw.Core/Automation/ProcessApprovalGate.cs` — suspends the provider subprocess while approval is pending and fails closed when suspension is unavailable.
- `KittyClaw.Web/Components/Pages/Approvals.razor` — displays pending requests, temporary decisions, and audit history.

## Entry points

- `POST /api/projects/{slug}/approvals/requests` registers a request and pauses its matching active run.
- `POST /api/projects/{slug}/approvals/decisions` records a temporary decision and resumes the matching run.
- `GET /api/projects/{slug}/approvals/requests` and `/decisions` expose the consultable audit data.
- The project navigation opens `/p/{slug}/approvals` for the approval queue and history.

## External dependencies

- The run lifecycle and provider subprocess integration described in [agent dispatch](./agent-dispatch.md).
- The project approval registry stored alongside other project data described in [storage](./storage.md).
- The HTTP surface documented through the generated [REST API](./rest-api.md).
