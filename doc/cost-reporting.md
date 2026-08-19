# Cost reporting

## Purpose

The global Costs page aggregates durable agent-cost records across registered projects. It shows totals, daily project series, and project breakdowns for an inclusive date range, with optional project and pipeline filters while preserving historical project and pipeline attribution. Each project's daily chart stacks, per date, the measured cost, the estimated cost, and the estimated dollar value of RTK savings; a day whose RTK savings cannot be priced (no known model rate over the period) shows a token-only marker instead of a fabricated dollar segment.

## Key components

- `KittyClaw.Core/Automation/CostTracker.cs` — defines the backward-compatible durable cost-log record.
- `KittyClaw.Core/Automation/RunCostRecorder.cs` — records project and pipeline data when a run finishes, then requests a coalesced snapshot refresh.
- `KittyClaw.Core/Services/CostReportService.cs` — serves filter options and reports from a compact persistent snapshot and an in-memory filtered-report cache. Its sequential refresh pass reads current and rotated JSONL logs, ignores malformed records, deduplicates runs, and resolves legacy pipeline attribution outside the UI request path.
- `KittyClaw.Core/Services/CostReportRefreshService.cs` — performs the initial asynchronous refresh, reacts to coalesced new-cost notifications, and provides a 15-minute fallback refresh.
- `KittyClaw.Web/Components/Pages/Costs.razor` — renders totals, filters, the stacked daily chart (measured, estimated, RTK savings segments), project breakdown, and loading, empty, estimated, and error states.

## Entry points

- The main navigation opens the global `/costs` route without requiring a selected project.
- The page reads filter options and report data directly from the current `CostReportService` snapshot. Filter changes only aggregate the compact in-memory rows and never rescan historical logs.

## External dependencies

- Registered project metadata and workspace resolution from `ProjectService`.
- Pipeline and ticket services for current options and backward-compatible resolution of legacy records.
- Durable current and rotated `cost-log*.jsonl` files under each project workspace's `.agents/channel/` directory.
- The atomic `cost-report-snapshot.json` cache in the KittyClaw data directory, retained across process restarts.
- Blazor Server for the interactive page and accessible daily bar visualization.
