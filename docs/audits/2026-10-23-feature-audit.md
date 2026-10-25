# KittyClaw feature audit — 2026-10-23

This is the English repository copy of the feature audit delivered in the conversation on
2026-10-23. Scores describe the state examined before the follow-up fixes. They are a
historical assessment, not a claim that every path has been tested or that the findings
remain unfixed. Consult the architecture documentation for current behavior.

## Assessment and method

KittyClaw is already useful for orchestrating local agent work. It is an advanced beta:
the core is substantial, while some cross-cutting guarantees and user journeys remain
incomplete. Consolidating existing behavior should precede further feature expansion.

The audit examined documentation, implementation, a partial test run, and synthetic
scenarios against temporary databases. Graphical journeys and real provider integrations
were not verified exhaustively. The checkout changed during the audit.

Scores are subjective and range from 1 to 5 for an owner managing several agent projects:

- **U**: practical usefulness.
- **F**: expected functionality from implementation and available verification.
- **T**: polish and completion of the advertised scope.

A high F score does not mean every journey was exercised during this audit.

## Projects and onboarding

| Feature | U | F | T | Assessment |
| --- | :-: | :-: | :-: | --- |
| Project creation, settings and deletion | 5 | 4 | 4 | Essential; deleting a project retains its workspace. |
| Integrated folder browser | 4 | 4 | 4 | Avoids native-dialog friction across platforms. |
| Workspace and Git initialization | 5 | 4 | 4 | Substantially reduces manual setup. |
| Agent template installation | 5 | 4 | 4 | Prepares instructions, skills and memory. |
| CLI and prerequisite detection | 5 | 4 | 4 | Useful; installation and authentication largely remain external. |
| Workflow design wizard | 5 | 4 | 3 | Valuable, but generated proposals require review. |
| First-ticket and first-run journey | 5 | 4 | 3 | Outcome-oriented; technical steps remain. |
| Legacy workflow migration | 4 | 4 | 3 | Necessary compatibility with additional complexity. |
| Project pause and resume | 5 | 4 | 4 | Essential control over background work. |
| Unified multi-project view | 5 | 4 | 4 | Especially useful with concurrent projects. |

## Kanban and tickets

| Feature | U | F | T | Assessment |
| --- | :-: | :-: | :-: | --- |
| Ticket creation and editing | 5 | 4 | 4 | Well-developed core functionality. |
| Custom columns | 5 | 4 | 4 | Names, colors, ordering and semantic roles. |
| Ticket moves and ordering | 5 | 4 | 4 | Routing restrictions apply to interactive moves. |
| Contextual column editor | 5 | 4 | 4 | Configuration directly from the board. |
| Priorities, labels, members and assignments | 5 | 4 | 4 | Coherent work organization. |
| Advanced search and filters | 5 | 4 | 4 | Number, author, assignee, priority, label and date queries. |
| Column sorting | 4 | 4 | 4 | Multiple criteria, including agent consumption. |
| Comments and activity history | 5 | 4 | 4 | Essential for understanding and resuming work. |
| Markdown, mentions and cross-project references | 4 | 4 | 4 | Connects discussions to tickets. |
| Description and comment images | 4 | 4 | 4 | Supports visual bugs, mockups and evidence. |
| Child tickets and parent progress | 5 | 4 | 4 | Work decomposition and blocking-child waits. |
| Ticket dependencies | 5 | 4 | 3 | Useful dispatch gates; transfer preservation is defective. |
| Structured acceptance criteria | 5 | 4 | 3 | Independent assessments and evidence; translation/test alignment needed. |
| Future ticket scheduling | 4 | 4 | 4 | Automatic promotion respects project pause. |
| Read and activity indicators | 4 | 4 | 3 | Helpful across pipelines; graphical validation remains incomplete. |
| Cross-project transfer | 5 | 2 | 2 | Implemented, but the lossless promise fails for dependencies. |
| Mobile interface | 4 | 3 | 2 | Adaptations present; translation and device verification incomplete. |

## Workflows and automation

| Feature | U | F | T | Assessment |
| --- | :-: | :-: | :-: | --- |
| Multiple pipelines per project | 5 | 4 | 4 | Stable identities independent of display names. |
| Generic column agents | 5 | 4 | 4 | The central product differentiator. |
| Processor mission, prompt, model and skills | 5 | 4 | 4 | Powerful but dense configuration. |
| Available, recommended and mandatory skills | 5 | 4 | 4 | Reusable capabilities; reported use is not proof of correct use. |
| Ordered before/after actions | 5 | 4 | 4 | Supports more deterministic workflows. |
| Agent-outcome routing | 5 | 4 | 4 | Explicit business destinations, including other pipelines. |
| Ticket selection ordering | 4 | 4 | 4 | Controls work queues. |
| Bounded delayed retries | 5 | 4 | 4 | Handles temporary failures. |
| Interruption recovery and checkpoints | 5 | 4 | 4 | One of the strongest architectural areas. |
| Loop and duplicate-work protection | 5 | 4 | 4 | Durable mechanisms with diagnostics. |
| Waiting for children and automatic resumption | 5 | 4 | 4 | Supports decomposed workflows. |
| Column cron tasks and scheduled actions | 4 | 4 | 4 | Useful for recurring maintenance. |
| Legacy automation engine | 3 | 4 | 3 | Rich, but coexistence increases complexity. |
| PowerShell scripts and HTTP actions | 4 | 4 | 3 | Useful extensions requiring deliberate configuration. |
| Portable pipeline kit export/import | 4 | 4 | 4 | Preview, archive validation and controlled installation. |

The legacy engine covers column presence, status changes, comments, child status,
Git commits, intervals and inactivity. Actions include agents, moves, assignments,
labels, comments, ticket creation, scripts, HTTP and memory maintenance.

## Agent execution and memory

| Feature | U | F | T | Assessment |
| --- | :-: | :-: | :-: | --- |
| Claude Code | 5 | 4 | 4 | Broadest integration, especially interactions and permissions. |
| OpenAI Codex | 5 | 4 | 3 | Dispatch and event normalization; different pre-effect protection. |
| Grok Build | 3 | 3 | 3 | Adapter present; documented stream and interaction limitations. |
| Mistral Vibe | 3 | 4 | 3 | Real alternative without full provider parity. |
| Ollama/local models | 4 | 3 | 3 | Privacy/cost value; depends on Claude transport and model capability. |
| DeepSeek | 3 | 4 | 3 | Dedicated vault-backed integration with transport-specific constraints. |
| Project, member and processor model selection | 5 | 4 | 4 | Flexible; precedence must remain understandable. |
| Fallback models/provider switching | 5 | 4 | 3 | Resilience depends on provider error detection. |
| Live run output | 5 | 4 | 4 | Essential operational visibility. |
| Stop, retry and steering | 5 | 4 | 4 | Concrete owner control. |
| Ad-hoc chat, history and images | 4 | 4 | 4 | Useful outside normal workflows. |
| Interactive agent questions | 4 | 3 | 3 | More complete through Claude. |
| Persistent sessions and conversation handoff | 5 | 4 | 4 | Avoids restarting context for every intervention. |
| Agent memory and consolidation | 5 | 4 | 4 | Continuity across runs. |
| Native processor memory capitalization | 5 | 4 | 4 | Durable checkpoints avoid rerunning completed business work. |
| Optional RTK shell-output optimization | 3 | 4 | 3 | Helpful, secondary to workflow reliability. |

## Evidence, owner decisions and JEV

| Feature | U | F | T | Assessment |
| --- | :-: | :-: | :-: | --- |
| Execution evidence capture | 5 | 4 | 4 | Commands, tests and repository state with provenance. |
| Verified evidence versus agent claims | 5 | 4 | 4 | A particularly valuable product direction. |
| Owner decision briefs | 5 | 4 | 4 | Supports acceptance, correction and stopping. |
| Missing/stale evidence detection | 5 | 4 | 4 | Prevents old results masquerading as current verification. |
| Processor acceptance assessments | 5 | 4 | 3 | Structured atomic reports remain agent claims. |
| JEV qualification and triage | 4 | 4 | 3 | Useful routing assistance; project-level benefit needs measurement. |
| JEV route proposals and application | 4 | 4 | 3 | Bounded choices, thresholds and audit records. |
| JEV post-run comparison | 3 | 4 | 3 | Routing analysis with added cognitive overhead. |
| JEV evidence-backed acceptance review | 5 | 4 | 3 | Strong potential, dependent on captured evidence quality. |
| JEV authorized model/skills profiles | 4 | 4 | 3 | Owner-approved profiles add configuration. |
| Decision history and reasoned rejection | 5 | 4 | 4 | Good revision, staleness and replay handling. |
| Temporary permissions | 5 | 4 | 3 | Scoped approvals, expiry and audit history. |
| Pre-effect boundary enforcement | 5 | 3 | 3 | Real protection with provider and visibility limits. |

Ticket approval and technical prevention of publication are separate mechanisms.
A review column alone does not guarantee prevention of external provider effects.
The examined capability matrix advertises interception for Claude and Ollama through
Claude. Codex, Grok, Mistral and DeepSeek remain observation-only and are refused in
strict mode. Opaque tool/command effects remain an explicit exclusion even with Claude.
See [runtime enforcement](../runtime-boundary-enforcement.md).

## Dashboard, costs and operations

| Feature | U | F | T | Assessment |
| --- | :-: | :-: | :-: | --- |
| Per-project dashboard | 4 | 4 | 4 | Useful complementary supervision. |
| Tile positioning, sizing and persistence | 4 | 4 | 4 | Complete in principle. |
| Fifteen tile renderers | 4 | 4 | 4 | Markdown, tables, KPI, charts, progress, heatmaps, timelines, images and Mermaid. |
| Chat-based tile creation/editing | 4 | 4 | 3 | Depends on correct generated files. |
| Script/agent tile refresh | 4 | 4 | 3 | Powerful; recurring costs need control. |
| Interval/daily refresh schedules | 4 | 4 | 4 | Persistence, catch-up and project pause handling. |
| Dashboard output through worktrees | 4 | 3 | 3 | Audit-time HTTP 500; corrected in the dashboard follow-up below. |
| Global cost reporting | 5 | 4 | 4 | Date/project/pipeline/model filters and history cache. |
| Measured versus estimated costs | 5 | 4 | 4 | Transparent, but not a provider invoice. |
| JEV costs and RTK savings | 4 | 4 | 3 | Useful additions with explicit estimates. |
| Daily budget cap | 5 | 2 | 2 | Found in the legacy engine rather than a universal dispatch gate. |
| Root-ticket worktrees | 5 | 4 | 4 | Valuable concurrent-work isolation. |
| Durable Git integration queue | 5 | 4 | 4 | Structured integration and recovery. |
| Local checkout synchronization/recovery | 5 | 4 | 4 | Correct distinction between integrated commit and synchronized checkout. |
| Project secret vault | 5 | 4 | 4 | Native encryption, write-only UI and subprocess injection. |
| REST and generated OpenAPI docs | 5 | 4 | 4 | Broad surface; outdated route-inventory test corrected in the documentation follow-up below. |
| Embedded MCP server | 5 | 4 | 4 | Valuable agent-to-orchestrator integration. |
| Engine health and queue diagnostics | 5 | 4 | 4 | Reduces unexplained silent stalls. |
| Local storage and migrations | 5 | 4 | 4 | Fits individual local usage. |
| Seven-language UI | 4 | 3 | 3 | Audit-time translation gaps corrected in the documentation follow-up below. |
| Windows installer and tray lifetime | 5 | 4 | 4 | Substantially lowers installation friction. |
| Updates coordinated with active work | 5 | 4 | 4 | Separates download from installation and protects active work. |
| First-project activation metrics | 3 | 4 | 3 | Helps improve onboarding; does not prove present effectiveness. |
| Daily telemetry | 2 | 4 | 3 | Product value; no opt-out conflicts with some local-use expectations. |
| QA tools, mock CLI and isolated instances | 5 | 4 | 3 | Good investment; some tests depend on fixed build paths. |

## Priority findings at audit time

1. **Transfers lose dependencies.** A synthetic blocked ticket had one blocker before
   transfer and none afterward. Preserve compatible internal edges or reject transfers
   that would split a dependency. Target pipeline mapping is also restricted to the
   default pipeline. See [TicketTransferService](../../KittyClaw.Core/Services/TicketTransferService.cs).
2. **The daily budget is not universal.** The gate was found in `RunStateManager`,
   including a `ceo` exemption, without equivalent centralized protection for columns,
   chat or dashboard runs. Check all launches centrally, including retries and queued
   work, and document the treatment of already-running work.
3. **Provider capabilities need to be visible when selecting a model.** Interactions,
   resumption and permissions differ; surface compatibility before dispatch.
4. **Investigate dashboard/worktree output.** The isolated `PutOutput_UsesMaintenanceWorktree_WhenProjectWorktreesAreEnabled`
   test returned HTTP 500. The cause was unresolved at audit time; the dashboard follow-up
   below records the subsequent reproduction and correction.
5. **Align docs, translations and tests.** README advertised seven MCP tools while code
   declared 78. The OpenAPI baseline lacked three acceptance-criteria routes.
   Localization key parity failed; examined mobile additions had English/French only.
   The documentation follow-up below records the verified correction of these gaps.

## Verification limits

An isolated build succeeded. The general suite was manually stopped after 8 minutes
40 seconds: **958 passed, 11 failed, 6 skipped**, with the rest unverified.
Eight failures were source-path assumptions incompatible with the relocated build,
one was the outdated route inventory, one localization parity, and one the dashboard
HTTP 500. These are neither a complete green suite nor eleven confirmed product bugs.
Raw test logs, temporary databases and private local paths are intentionally excluded
from this document and published source.

## Product recommendation

The strongest chain is **ticket → column agent → evidence → owner decision**, supported
by persistent memory and Git isolation. Dashboard, JEV and provider breadth enrich
that chain while adding configuration. Prioritize dependency-safe transfers and a
universal budget gate, then permission clarity, first-workflow simplicity, and mobile
and multilingual completion.

## Follow-up fixes — 2026-10-23

The two priority defects were subsequently addressed without rewriting the historical scores:

- Transfers now preserve dependency edges within the transferred tree, including their
  direction and original creation timestamps, in the same transaction as the tickets.
  An edge crossing the tree boundary in either direction rejects the transfer before
  content writes. See [current transfer behavior](../ticket-transfer.md).
- `AgentBudgetService` checks every application agent launch against the project's
  daily budget, including chat, columns, dashboard, planning, memory work, retries,
  queued work, fallback and steering continuations. The `ceo` exemption was removed.
  Known usage awaiting asynchronous recording is included without double counting;
  invalid/unreadable configuration blocks admission. Already-running work is not
  terminated, and unreported/unknown charges cannot be reserved in advance.
  See [budget admission](../agent-dispatch.md#daily-budget-admission).

Verification: **82 targeted tests passed, none failed or skipped**, covering transfer
fidelity/rejection/rollback, budgets and the production service registration, runner
lifecycle with mock providers, usage accounting, reports, JEV cost recording and session
storage. The repository data-policy scan and its six synthetic self-tests also passed.
These targeted checks do not replace a complete end-to-end product validation.

## Dashboard worktree follow-up — 2026-10-23

**Status: resolved and verified end to end.** The original scores above remain a record
of the pre-fix audit.

The HTTP 500 was reproduced in an isolated application. Git for Windows failed while
creating the maintenance worktree with `fatal: '$GIT_DIR' too big`, before writing tile
output. The full project slug was repeated in the generated worktree directory and
Git's administrative path. A synthetic Git reproduction failed with the original name
both with default settings and with `core.longpaths=true`; the same repository accepted
a shorter worktree name.

`ProjectWorktreePaths` now bounds newly generated maintenance, integration and quarantine
directory names to 40 characters, retaining a readable prefix plus a deterministic
hash suffix for long names. Branch names and project identities remain stable. Registered
worktrees from previous versions are reused at their original paths. The HTTP regression
also reads the actual worktree path from the durable queue instead of assuming its name.

The browser journey uses the real application on an ephemeral local port and synthetic
data. It verifies the output write, isolated maintenance commit, integration tip,
background synchronization after project resume, updated dashboard rendering and reuse
for an unchanged write. No real agent provider or main application data is involved.
See [dashboard verification](../dashboard.md#output-writes-with-worktrees-enabled)
and [worktree naming](../durable-worktree-integrations.md).

Verification: **104 tests passed, none failed or skipped**, covering the HTTP regression,
the real-browser journey, existing-worktree compatibility, durable routing, integration
queue recovery/conflicts/quarantine and dashboard write finalization. The browser journey
ran on Windows with headless Edge and the actual hosted synchronization worker:

1. The tile initially rendered the original output.
2. The HTTP write returned 204, committed the new output in the maintenance worktree and
   persisted a Pending integration request. The primary checkout, its HEAD and its
   displayed output were unchanged.
3. Integration completed and Git contained the new output at the integrated commit,
   while the primary checkout still held the original output.
4. Resuming the project let the background worker complete local synchronization.
   The checkout HEAD matched the integrated commit and the output API returned the new text.
5. Reloading the dashboard rendered the updated tile without browser errors.
6. Repeating the unchanged write returned 204, reused the same maintenance worktree and
   left the integration row Completed and the checkout clean.

The repository data-policy scan and its six synthetic self-tests passed. The browser
screenshot is explicitly ignored by Git; raw logs and temporary databases are not
included in the report. This closes the specific HTTP 500 finding without claiming
complete validation of every dashboard capability.

## Localization, MCP and OpenAPI follow-up — 2026-10-23

**Status: resolved and verified.** Commit `88f2b73` records the preceding transfer,
budget and dashboard fixes. Historical audit scores remain unchanged.

The localization inventory found 60 missing translations across Spanish, German,
Italian, Brazilian Portuguese and Japanese: two acceptance-processor strings, six
cost-reporting strings and four mobile-board strings per language. All were translated,
including the five missing mobile bundles. The existing parity guard now passes for
all 18 bundles, 1,443 reference keys and seven supported languages (126 resource files),
checking key sets and format placeholders. Runtime language loading and the English
fallback also pass. This establishes resource completeness; it is not a linguistic
review or a visual review of every translated screen.

The [README](../../README.md#mcp-server) now describes the current MCP management
surface and its 78 tools. The [detailed catalogue](../mcp.md) documents all
nine board/acceptance tools and 69 management/observability tools, including criterion
creation, updates, evidence retention and deletion. A catalogue comparison found all
78 declared tools documented with no missing or extra names. The existing real MCP
client test was also updated to expect the two acceptance tools and confirms the
complete catalogue over isolated Streamable HTTP.

The explicit OpenAPI baseline now includes acceptance-criterion creation, update and
deletion, with their expected `Tickets` tags. Exact route-set comparison is retained;
the current live isolated specification matches all 132 expected operations. The REST
acceptance-criteria round trip also verifies evidence, invalid input and deletion.

Verification: **56 targeted tests passed, none failed or skipped**, covering resource
parity/loading/fallback, route inventory/tags, endpoint smoke tests, generated OpenAPI
documentation, MCP schemas/catalogue/workflows/guards and acceptance-criteria HTTP
behavior. The test run rebuilt the affected projects without compiler errors. The
repository data-policy scan and its six synthetic self-tests passed. These results
do not claim a complete green product suite.

Temporary isolated builds, test-data copies, screenshots and one-off diagnostic probes
under `.artifacts/` were reviewed for cleanup. With no tracked files, external reparse
points or active consumers, all 11,765 temporary files (3.52 GB) were removed to the
Windows Recycle Bin. That directory is now excluded from Git; maintained regression
tests and this report remain versioned.
