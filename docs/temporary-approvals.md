# Temporary approvals

## Purpose

Agent permissions let an operator review a protected action with its resource, reason, scope, duration, provider, run, and ticket context. Projects default to observation and can enable required authorization from project settings. Decisions are limited to allow once, allow for the current ticket and matching action/resource for at most 24 hours, or deny. Requests, decisions, and effect receipts are persisted for audit without storing sensitive arguments in the operator view.

## Key components

- `KittyClaw.Core/Services/ApprovalRegistryService.cs` — persists requests, decisions, receipts, expiry, duplicate suppression, and audit history.
- `KittyClaw.Core/Services/ApprovalWorkflowService.cs` — correlates persisted requests and decisions with active runs.
- `KittyClaw.Core/Automation/AgentRun.cs` — tracks the pending request and exposes the correlated wait/resume gate.
- `KittyClaw.Core/Automation/ProcessApprovalGate.cs` — suspends the provider subprocess while approval is pending and fails closed when suspension is unavailable.
- `KittyClaw.Web/Components/Pages/Approvals.razor` — separates actionable requests from localized decision and effect history.
- `KittyClaw.Web/Components/Pages/ProjectSettings.razor` — configures observation or required authorization and explains provider compatibility.
- `KittyClaw.Web/Components/Pages/Board.razor` — displays the live count of pending permission requests.

## Entry points

- `POST /api/projects/{slug}/approvals/requests` registers a request and pauses its matching active run.
- `POST /api/projects/{slug}/approvals/decisions` records a temporary decision and resumes the matching run.
- `GET /api/projects/{slug}/approvals/requests` and `/decisions` expose the consultable audit data.
- Project settings persist the enforcement mode through `PATCH /api/projects/{slug}`.
- The project navigation opens `/board/{slug}/approvals` for the pending queue and history.

## External dependencies

- The run lifecycle and provider subprocess integration described in [agent dispatch](./agent-dispatch.md).
- The project approval registry stored alongside other project data described in [storage](./storage.md).
- The HTTP surface documented through the generated [REST API](./rest-api.md).
