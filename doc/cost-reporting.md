# Cost reporting

## Purpose

The global Costs page aggregates durable agent-cost records across registered projects. It shows totals, daily project series, and project breakdowns for an inclusive date range, with optional project and pipeline filters while preserving historical project and pipeline attribution.

## Key components

- `KittyClaw.Core/Automation/CostTracker.cs` — defines the backward-compatible durable cost-log record.
- `KittyClaw.Core/Automation/RunCostRecorder.cs` — records project and pipeline snapshots when a run finishes.
- `KittyClaw.Core/Services/CostReportService.cs` — reads current and rotated JSONL logs, ignores malformed records, deduplicates runs, resolves legacy pipeline attribution, and aggregates report data.
- `KittyClaw.Web/Components/Pages/Costs.razor` — renders totals, filters, daily chart, project breakdown, and loading, empty, estimated, and error states.

## Entry points

- The main navigation opens the global `/costs` route without requiring a selected project.
- The page loads filter options and report data directly through `CostReportService` and refreshes all displayed aggregates when filters change.

## External dependencies

- Registered project metadata and workspace resolution from `ProjectService`.
- Pipeline and ticket services for current options and backward-compatible resolution of legacy records.
- Durable current and rotated `cost-log*.jsonl` files under each project workspace's `.agents/channel/` directory.
- Blazor Server for the interactive page and accessible daily bar visualization.
