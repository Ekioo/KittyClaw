# JEV qualification and routing

## Purpose

A column processor can opt into `JevObservationOnly`. This replaces normal agent processing
for that column with a routing suggestion. Agent dispatch, before/after actions, and
column execution claims are bypassed. Configured business routes are applied automatically when JEV meets the column confidence threshold. Independent scheduled tasks
and legacy automations retain their own configuration.

## Shared uncertainty fallback

Every JEV mode uses the configured confidence threshold. Errors, missing credentials,
abstentions, insufficient acceptance evidence and low-confidence answers resume the equivalent
classic processing. Qualification-only columns run their normal agent and routing; execution
profile selection uses the column's normal model and skills; acceptance validation uses the
normal column processor and manual transition rules. A pending or stale observation is refreshed
before making this choice. Confident negative acceptance judgments still require owner review.
Fallback is recorded on the durable execution so retries keep the same classic behavior without
changing the saved column configuration. Required skills and success-context checks still apply.

## Configuration

In **Configure column → Processor**, enable **JEV qualification**.
Set the mission and optional instructions, and configure outcome routes or a default destination
in the Routing tab. JEV is optional: the option is disabled until a credential is detected.
Add a key in **Settings → JEV / TypeSafe**, set `TYPESAFE_API_KEY` in the machine environment,
or add a project-specific override through the existing project secret vault.
No JEV executable is required. Detection only checks local configuration; it does not validate
credentials remotely, call TypeSafe, or activate processors automatically.

Credential priority is project vault, KittyClaw settings, then machine environment. On Windows,
the process, user and machine environment scopes are checked in that order, allowing detection
of user variables added after KittyClaw started. Other platforms use the process environment.
The global key is encrypted using the existing native secret protector and stored below
`<data-dir>/integrations/secrets/typesafe.vault`. It is isolated from project vaults and is never
injected into agent subprocesses. The settings UI exposes presence and source only, supports
replacement and removal, clears the password input after saving, and never retrieves the value.
A saved observation processor with missing credentials remains in observation mode with an
unavailable diagnostic; it cannot silently fall back to an agent or move the ticket.
The REST processor contract also accepts `jevObservationOnly`; omitting it preserves the saved mode.
The flag is versioned in the authoritative `processor.json` and projected into SQLite.

Ticket title, description, priority and the latest twenty comments are sent to TypeSafe, together
with the column mission, instructions and candidate destination names/outcomes. Contexts above
48,000 characters are rejected locally. Only explicitly configured business routes/defaults are
candidates; technical/action failure routes are excluded. `none` is always available.

## Structured triage

Each qualification uses one TypeSafe request containing four independent questions over the
same evidence: routing, primary request type, complexity category and whether owner clarification
is needed before proceeding. Types are problem/incident, new work, improvement/maintenance,
question/investigation and other/unknown. Complexity is simple, moderate, complex or unknown;
it does not estimate duration or urgency. Clarification is displayed as a yes-probability,
without converting it into an automatic decision or inventing a free-text explanation.

The ticket shows the estimates, Choice confidence and distributions, plus available input/output
token usage and API duration. Triage does not change ticket fields; a sufficiently confident route is applied by the routing policy.
Every required answer is validated before publishing the observation; a malformed triage answer
cannot leave a seemingly valid routing proposal. Question version `qualification-v3` invalidates
the previous fingerprint so existing enabled observation columns are requalified once. Stored
routing-only observations remain readable and are shown without fabricated triage until refreshed.
Adding questions consumes tokens; batching avoids separate calls but does not make those questions free.

## Components and lifecycle

- `JevObservationEngine` waits for application startup and scans enabled processors every 15 seconds.
  Paused projects are excluded. Calls are sequential, with a 30-second timeout.
- `JevQualificationService` calls the fixed TypeSafe HTTPS endpoint with `jev-latest`, validates
  the closed-set Choice response and stores the latest observation per ticket in the project
  SQLite `JevObservations` table. Response bodies and credentials are not included in errors.
- A fingerprint of the inputs and question version deduplicates unchanged observations across
  restarts. Failed observations retry after five minutes. Changed inputs trigger a fresh observation.
- `JevProposalPanel` refreshes every ten seconds and shows the proposed destination, confidence,
  probability distribution, resolved model, timestamp or actionable configuration error. It marks
  observations stale when ticket inputs, routes or processor configuration change.

Confidence describes the answer distribution, not a guarantee of correctness. The optional routing
policy uses this returned confidence, rather than the selected option’s probability. No live TypeSafe call is needed by the test suite:
`JevQualificationTests` uses an injected HTTP handler and an isolated secret vault/database.

## External dependency

[TypeSafe System One HTTP API](https://docs.typesafe.ai/api/system-one), using the
[Choice primitive](https://docs.typesafe.ai/primitives/choice).

## Debug browser verification

`JevObservationJourneyTests` starts the real Blazor/Kestrel app on an ephemeral port with
isolated data under `%APPDATA%/KittyClaw-debug/jev-e2e-*`. It replaces TypeSafe with a
fixture, disables host-key detection and saves a fake global key through settings. The
browser configures qualification while leaving the processor disabled, asks for a current
recommendation, checks deduplication and manually applies it. Reload retains the JEV entry
alongside comments. Enabling the processor then proves automatic routing for a second ticket
without the legacy automatic-routing flag. Screenshots and debug data are retained there.
No real project data, TypeSafe requests or provider runs are used.

```powershell
$env:KITTYCLAW_JEV_DEBUG_E2E = '1'
dotnet test KittyClaw.Core.Tests --filter FullyQualifiedName~JevObservationJourneyTests
Remove-Item Env:KITTYCLAW_JEV_DEBUG_E2E
```

## Routing proposals after agent execution

Regular agent columns can separately enable **JEV routing proposals after agent runs**.
This mode is mutually exclusive with qualification-only processing. Normal agent execution, actions,
validation and routing remain authoritative. JEV is scheduled in its own background sweep, so a timeout,
missing key or malformed response cannot fail the agent execution or alter its workflow transition.

The authoritative processor definition stores `jevPostRunObservationSince`; the REST save contract
accepts `jevPostRunObservation` and preserves the timestamp when omitted. Only terminal column executions
with an agent run id ending after opt-in are observed. Ad-hoc chats and legacy agent runs without a column
execution are outside this feature because they have no source-column routing contract.

Each request asks only for routing among the source processor's business routes and default destination,
plus `none`. Context includes ticket state, recorded outcome, summary, error and agent-result claims,
and a matching per-run evidence bundle when available. Command output and raw command strings are omitted;
exit codes, test counts and provenance remain available. Evidence must match the project, ticket, run
and current claim time; merged evidence from other runs is never substituted. Missing/stale evidence and
agent claims are explicitly distinguished from independently captured observations.

Results live in `JevPostRunObservations`, separate from initial qualification. Each terminal execution
snapshot (id plus ending timestamp) has its own durable observation, including when a manual retry reuses
an execution id. Successful snapshots are not recomputed on unrelated ticket edits. If evidence arrives
late, a snapshot without evidence may refresh once. Failed requests use the existing five-minute backoff.
The ticket shows the source column, actual workflow destination, JEV suggestion, confidence, distribution,
execution/run ids, missing-evidence notice and historical-context marker when the ticket has changed.

## Human decisions with workflow effects

Current routing recommendations expose **Accept and apply destination** and **Reject and
requalify**. When automatic routing cannot apply a choice, explicit owner acceptance applies the proposed
column through `TicketService` and its existing manual-routing, assignment and scheduling rules.
The normal destination processor may consequently start. If the ticket is already at that destination,
acceptance records approval without replaying the transition or changing ticket freshness.

`JevProposalDecisionService` validates the exact proposal revision, the ticket snapshot, current source
routes and (for post-run proposals) the execution revision. A stale, missing, erroneous, superseded or
`none` proposal cannot be applied. The current column's manual routing policy remains authoritative.
Decision persistence, owner activity and the accepted transition share one SQLite transaction; status
signals fire only after commit. An audit-write failure rolls back the move. Repeated acceptance is
idempotent, while an opposite decision on an already-decided revision is rejected.

Rejection requires a reason and does not move the ticket, update its timestamp, add an owner comment or
raise agent-triggering events. Instead, rejected destinations are excluded from the next JEV request
for that same ticket/column/execution snapshot, and the owner's reason is included as feedback. If
alternatives remain, the existing observation sweep produces a replacement proposal. When no business
destination remains, it stops calling TypeSafe until the ticket context changes. A changed ticket can
be evaluated again; past decisions remain available in the audit history.

`JevDecisionStore` keeps an immutable `JevProposalDecisions` record keyed by the SHA-256 identity of the
complete proposal revision. It stores the exact proposal, accepted/rejected outcome, owner, UTC time,
reason and applied destination. Refreshing a model result cannot rewrite a past human decision. The
comment timeline shows past observations and decisions. Human UI actions invoke the manual
decision path; the observation worker uses the separately checked automatic path for configured
business routes.

## Automatic routing and the next-step recommendation

Business routes (outcome routes or a default destination) enable automatic JEV routing without
requiring the legacy `jevAutomaticRouting` switch. The configured confidence threshold is still
used (90% by default). Sufficiently confident acceptance reviews remain explicit owner decisions.

In qualification-only columns, `JevObservationEngine` evaluates tickets and applies eligible
choices through `JevProposalDecisionService`. For ordinary agent columns with a detected TypeSafe
credential, `ColumnProcessingEngine` asks JEV after the agent result and memory capitalization,
before the normal workflow transition. The agent result is included as unverified claims.
JEV chooses only among configured business destinations plus `none`. Scheduling and waiting
for child tickets retain their deterministic contracts. Without a detected credential, ordinary
agent columns retain their existing outcome routing.

A confident JEV choice determines the execution's actual destination; the agent's original
outcome remains in the execution record. Required skills, success evidence, ticket freshness,
assignment and scheduling checks still apply. The exact JEV choice, threshold and automatic
audit record are committed with the ticket move and execution completion. Audit failure rolls
back the move. A repeated automatic source/destination transition within ten minutes is refused.
The ordinary success guard also runs when JEV selects a success column that differs from the
agent's proposed destination, before configured side effects.

An API error, `none`, or confidence below the threshold falls back to the ordinary agent
outcome/default routing. Missing credentials also preserve classic routing. No additional
agent dispatch or manual hold is introduced by this fallback.
Post-run observations are historical comparisons, not a second automatic routing pass.

The ticket presents one compact **JEV · Next step** area with **What should I do with this
ticket?**. It requests a fresh recommendation from the ticket's current routing context;
unchanged inputs use the stored result. Only an undecided recommendation matching the current
column, ticket and processor fingerprint appears here. Rejected, applied and outdated results
appear as JEV entries alongside comments in the activity timeline, with technical details
collapsed and no stale action buttons. Decisions remain immutable in `JevProposalDecisions`;
timeline rendering does not add agent-triggering comments or mutate ticket freshness.

`AutomaticJevColumnRoutingTests` covers agent-before-routing order, destination selection,
uncertainty/abstention/API-error fallback, stale inputs, success evidence and audit rollback. The isolated
`JevObservationJourneyTests` exercises the on-demand control in a column whose processor is
disabled, comment history after reload, and automatic application after enabling qualification,
without the legacy automatic-routing flag.
Use `KITTYCLAW_JEV_DEBUG_E2E=1` for that browser journey; it uses fixture providers only.

## Acceptance criteria validation

A column can enable **JEV acceptance criteria validation** independently from routing qualification.
Both new work-review options default to off. A sufficiently confident validation replaces its implicit agent and
before/after actions with a manual review hold; it cannot combine this mode with routing observation,
automatic JEV routing or execution-profile selection. Finish or cancel active column executions before
enabling it. Configure the default destination for owner approval and an outcome route named
`changes_requested` for corrections. Without that correction route, rejection records the review and
keeps the ticket in the validation column until its context or configuration changes.

Criteria are the union of shared column criteria (one per line) and Markdown task-checkbox text in
the ticket description. Checked boxes are requirements, never proof of completion. At most twenty
criteria of 2,000 characters each are accepted. No explicit criteria produces a visible diagnostic.
The REST configuration fields are `jevAcceptanceValidation` and `jevAcceptanceCriteria`; omitted
fields preserve the authoritative `processor.json` values and SQLite projection.

`JevWorkReviewEngine` scans enabled work-review columns every fifteen seconds, excluding paused
projects. `JevWorkReviewService` loads evidence for the latest terminal run linked to this ticket;
when no run exists it can use the ticket evidence bundle. Project, ticket, run identity and capture
freshness are checked. Each evidence item retains its provenance, timestamp and run id. Stale,
contradictory, missing and agent-claimed evidence cannot support a verified criterion. Matching
command exit/test counts, up to fifteen bounded file-diff excerpts and repository state are supplied;
raw command strings and command output are excluded. Input is capped at 48,000 characters.

One batched TypeSafe request asks two independent Choice questions per criterion: satisfied/unmet/
insufficient, and the strongest reference from actual current verified evidence plus `none`.
An absent reference downgrades the result to insufficient. An invented reference, malformed response
or API error invalidates the complete result. Without usable evidence, all criteria are marked
insufficient locally without spending an API call. The ticket shows each criterion, its status,
available model confidence, evidence reference, all captured evidence and the count of unmet or
unverified criteria. This identifies missing verification as well as missing behavior.

For sufficiently confident reviews, only owner actions apply outcomes. **Approve and continue** applies the configured default destination.
Overriding any unmet/unverified criterion requires a reason. **Request corrections** requires a reason,
applies the configured `changes_requested` route when present, and adds owner feedback listing the
missing criteria for the next agent. Direct moves out of an enabled validation column through
`TicketService` are held until this review action is taken. Approval/rejection, owner activity and
any move/comment are committed atomically; audit failure rolls back the move. Duplicate decisions
are idempotent; opposite decisions, stale ticket/configuration/evidence or active executions are rejected.
There is no automatic acceptance mode or confidence threshold that bypasses owner approval.

`JevWorkReviews` stores the current exact revision per ticket/column. `JevWorkDecisions` retains
immutable decision snapshots, owner reason and applied destination. Both tables cascade with ticket
deletion. The current review and historical decisions, including their evidence, remain visible in
`JevWorkReviewPanel`. Changed inputs trigger another review; identical successful inputs are deduplicated
and errors back off for five minutes. Local missing-evidence assessments do not fabricate model certainty.

## Authorized model and skills profiles

Agent columns can enable **JEV model and skills selection** and configure one to ten execution profiles
with unique names, explicit models, descriptions and selected project skills. The model dropdown reuses
the existing provider catalogs. Descriptions should state the work each profile is suited for.
Profiles can reference only skills already configured in this column. Mandatory column skills are
always included in each saved profile, decision snapshot and execution. REST fields are
`jevExecutionSelection` and `jevExecutionProfiles`; omitted fields preserve existing values.

JEV chooses one authorized profile or `none` in a closed-set Choice, based on ticket intent, complexity
and column mission. It cannot invent a model or skill. The ticket shows the chosen model, skill set,
profile description and confidence. **Approve profile and allow execution** records the owner's
approval and lets the ordinary column engine claim that ticket. Until then, no agent or column action
runs for that ticket; other approved tickets in the candidate window can still progress. Rejecting
requires a reason, excludes that profile for the same context, and feeds the owner's reason into a new
selection. Once alternatives are exhausted no further API call is made until the context changes.

`ColumnExecutionService` validates approval against the current ticket/configuration and saves the
profile in `ColumnExecution.JevExecutionProfileJson` under its atomic claim. `ColumnProcessingEngine`
applies it to a private processor copy, preserving shared column defaults and required skills. The
snapshot survives retries/restarts and is rechecked against the authorized profiles before work starts.
The project fallback model is disabled for these approved profiles so execution cannot silently switch
to an unapproved model. Removed profiles fail closed; resolve the profile configuration before retrying.
Active executions do not trigger additional profile-selection requests from agent progress comments.
Ticket edits before claiming revoke approval and require review of a new proposal. All profile choices
and owner decisions remain in the ticket history after it moves to another column.

`JevWorkReviewTests` covers evidence provenance/freshness, missing and invented references, checked
checkboxes, owner overrides, correction feedback, stale contexts, deduplication, rollback, profile
approval/claims and authorized skills. The opt-in `JevWorkReviewJourneyTests` exercises the real debug
UI on an ephemeral port, with isolated data and deterministic JEV/agent fixtures. It enables both
options through the column dialog, builds an authorized profile, proves both manual holds, verifies
the effective model/skills while column defaults stay intact, requests corrections with the missing
criteria, and approves another ticket through to Done with historical evidence retained. Use
`KITTYCLAW_JEV_DEBUG_E2E=1`; no live TypeSafe or agent-provider calls are needed.

### Ticket presentation

The current JEV area leads with a next-step recommendation, criteria checklist or recommended profile, and the next owner action. Historical routing observations and decisions live in the comment timeline. Proofs, technical diagnostics, probabilities, and past decisions are collapsed by default. Rejection and acceptance overrides reveal a reason field only when needed. Column options live together under the JEV assistance disclosure.

### Live work review journey

`JevLiveWorkReviewJourneyTests` repeats the profile and acceptance UI journey against the real
TypeSafe API using the locally detected key. Opt in with `KITTYCLAW_JEV_LIVE_E2E=1` and filter
`FullyQualifiedName~JevLiveWorkReviewJourneyTests`. It uses isolated debug data and an ephemeral
port. Agent providers are replaced with an executor that runs four actual Python unittest checks,
including an intentional failure for the correction case. JEV selects between two authorized
profiles and judges the real process exit status and test counts. The test checks both manual
holds, applied model/skills, evidence references, owner correction feedback, acceptance to Done,
and retained history. Screenshots and raw verification output stay under the test's debug folder;
the local credential is neither copied into that folder nor logged.

API token usage and estimated JEV charges are included in [cost reporting](./cost-reporting.md).
