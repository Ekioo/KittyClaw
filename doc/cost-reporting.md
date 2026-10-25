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

## Provider rate verification (2026-10-25)

The scheduled verification added missing estimates for the already selectable Grok 4.7
and 4.6 models: $2 input, $6 output and $0.50 cached input per million tokens, doubled
at 200,000 prompt tokens, from the [official xAI rate card](https://docs.x.ai/developers/pricing).
These are standard global rates; Grok Build Fast and regional premiums are not inferred.
The current OpenAI flagship, Claude, DeepSeek, Mistral Medium 3.5 and TypeSafe prices
were checked against their official pages; no further price changes were established.
Legacy entries retain the verification limits described below.

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

## Session accounting and subscriptions (2026-10-23)

Codex `turn.completed.usage` is cumulative across the resumed thread. Before each subprocess,
the runner reads the last local rollout token counter for that session; the adapter charges only
the delta and advances its baseline. Repeated identical summaries contribute zero, and a fresh
fallback session starts from zero. A missing resume baseline remains unpriced rather than
charging the entire thread again. Cached input is a subset of input, and reasoning output is
already included in output; neither is added twice.

The rate card now includes all four GPT-6 selectors (Astra, 6.1 Sol, Sol, Luna), verified against
their official model pages on 2026-10-23. Standard short-context prices are used as the reference.
When rollout request telemetry is available, requests above 272,000 input tokens use twice the
input/cache rates and 1.5 times the output rate. Aggregate run tokens cannot establish this tier.
Speed tiers, regional premiums and separately billed tools are outside this reference estimate.

On background refresh, `CostHistoryAccounting` uses retained run timestamps/session IDs and
local Codex rollout counters (including `archived_sessions`) to reconstruct legacy request deltas. Corrected records are appended
as version 2 accounting revisions with the same run ID; the original evidence is retained.
Both reports and budget accounting select the highest accounting revision across current and
rotated journals. Snapshot version 6 invalidates old totals. Missing run/rollout evidence is
explicitly counted as unpriced. Repeated refreshes are idempotent. Run snapshots may be purged,
so successfully repaired records retain the corrected tokens, session, plan and rate-card version.

The page labels its total as **token usage value at API rates**, not actual spending. Codex rollout
`rate_limits.plan_type` detects the plan used for those requests (for example ChatGPT Pro), without
reading or exposing authentication credentials. Subscription usage is shown with its separate
API equivalent. The actual monthly payment, currency, tax, purchased-credit charges and any
allocation between projects cannot be inferred from these counters; no monthly fee is invented.
JEV remains a separate API usage breakdown. RTK shows hypothetical usage avoided, not a discount
on a subscription invoice.

`tools/RebuildCostReport` can explicitly rebuild an existing data directory with
`dotnet run --project tools/RebuildCostReport -- --apply <data-directory>`. It backs up the old
snapshot and starts no automation host or provider. Use it on an isolated copy first; only apply
to a live data directory after upgrading the app, because older binaries do not understand
accounting revisions. The new app also performs this rebuild automatically at startup.
For a live-safe preview with consistent SQLite backups, use
`dotnet run --project tools/RebuildCostReport -- --preview <source-data-directory> <new-preview-directory>`.
The preview copies only the reporting databases, run metadata and runtime cost journals, never
provider credentials or the project secret vault. The daily budget remains a cap on token value
at API rates; it does not estimate the remaining subscription allowance.

`CostAccountingTests` covers resume baselines, duplicate counters, fresh-session fallback,
missing evidence, all new rates, request context tiers, historical repair, subscription
attribution, journal revisions across rotation, budget deduplication and snapshot restart.

## JEV API accounting details

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
