# Cost reporting

## Purpose

The global Costs page aggregates durable agent and JEV cost records across registered projects. It shows totals, daily project series, and project breakdowns for an inclusive date range, with optional project and pipeline filters while preserving historical project and pipeline attribution. Each project's daily chart stacks, per date, the measured cost, the estimated cost, and the estimated dollar value of RTK savings; a day whose RTK savings cannot be priced (no known model rate over the period) shows a token-only marker instead of a fabricated dollar segment.

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

## Provider rate verification (2026-10-22)

`ModelCostEstimator` uses USD per million tokens, with provider-reported monetary totals
taking precedence. This refresh updates GPT-5.6 Sol to 4 input / 20 output / 0.4 cached
input / 5 cache write, from the [OpenAI standard rate card](https://developers.openai.com/api/docs/pricing).
The existing Terra, Luna, GPT-5.5 and GPT-5.4 entries are retained; this refresh does not
claim fresh verification of every legacy rate. OpenAI writes assume the 30-minute tier.

The [DeepSeek rate card](https://api-docs.deepseek.com/quick_start/pricing/) now lists
Pro at 1.32 input / 3.96 output / 0.044 cache hit, and Flash at 0.30 / 1.20 / 0.006.
The old Flash alias is billed as the current Flash model. Estimates use the peak ceiling:
usage summaries lack per-request timestamps and cannot apply the half-price off-peak schedule.
Cache creation, when separately reported, is charged as uncached input.

The [Claude rate card](https://platform.claude.com/docs/en/about-claude/pricing) covers
the four selectable models, including five-minute cache writes. Haiku 5.5 rates increase
fivefold above 100,000 input tokens, including cached tokens. Aggregate usage can cross
this threshold even when individual requests do not, so these remain estimates.

[Grok](https://docs.x.ai/developers/pricing) and
[Mistral Medium 3.5](https://docs.mistral.ai/inference/pricing) rates were confirmed unchanged.
Legacy Devstral rates are retained, not freshly verified. Regional, speed-tier, longer cache
retention, and separately billed tool fees are outside this token-only estimate.
Existing durable cost records are not repriced by this update.

## JEV API accounting

`JevCostRecorder` writes a separate `CostLogEntry` for each successful JEV API response, before
validating its judgments. Qualification, post-run routing, profile selection, and acceptance review
all use it. Requests returning invalid judgments still count; cached observations and owner decisions
do not create additional charges. No synthetic agent run or workflow event is emitted.
Records retain project/pipeline attribution, the resolved model, both token counts, a unique request
ID, and `Source = "jev"`. They request the normal coalesced snapshot refresh and contribute to the
workspace's existing budget accounting.

The page includes JEV in project totals and daily estimated segments. Each project also shows a
compact JEV breakdown with calls, input/output tokens, and the estimated amount at six decimal
places so small charges remain visible. Project, date, pipeline, and model filters apply to it.
Missing usage and unknown model rates are explicitly unpriced, rather than shown as free calls.
JEV input rates are excluded from RTK savings estimates because those savings concern agent CLIs.

The versioned rate card uses the [official TypeSafe model prices](https://docs.typesafe.ai/models),
verified on 2026-10-22: Jev 1.13.0 and 1.14.0 cost $0.042 per million input tokens; output tokens are
free. Monetary amounts derived from this tariff are estimates, not provider invoices. Unknown
future models and unresolved aliases remain unpriced.

During background refresh, recoverable legacy routing usage is imported from existing observations
and decision snapshots into the durable cost journal. Stable response IDs prevent duplicate charges
when the same observation also appears in its decision history. New observations carry their cost
record ID and are not imported twice. Importing preserves costs after observations are replaced.
Older work reviews did not retain token usage and cannot be retrospectively priced.

`JevCostRecorderTests` covers filters, rates, unpriced responses, invalid judgments, deduplication,
legacy imports, and restart durability. The isolated `JevWorkReviewJourneyTests` also checks that
four simulated API responses appear on `/costs`, including the model filter and precise amount.
