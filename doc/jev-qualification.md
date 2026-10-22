# JEV qualification and routing

## Purpose

A column processor can opt into `JevObservationOnly`. This replaces normal agent processing
for that column with a routing suggestion. Agent dispatch, before/after actions, and
column execution claims are bypassed. Manual validation is the default; automatic routing is a separate opt-in. Independent scheduled tasks
and legacy automations retain their own configuration.

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
token usage and API duration. No ticket field, priority, assignee or route is changed by triage.
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

The opt-in `JevObservationJourneyTests` starts the real Blazor/Kestrel app on port 5232,
uses an isolated Git repository and database below `%APPDATA%/KittyClaw-debug/jev-e2e-*`,
and replaces the TypeSafe HTTP client with a deterministic fixture while disabling host-key detection.
Playwright saves a fake key through global settings, verifies the cleared password field and zero
API calls before column activation. The project
starts with its processor disabled; Playwright activates observation and the processor
through the column dialog, then verifies the proposal in the ticket. It checks the ticket
stays in its source column for 32 seconds, no column execution is created, and only one
TypeSafe fixture request occurs. Screenshots and debug data are retained in that directory.
The test is skipped by default and requires a free port 5232 and installed Playwright Chromium.

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

`JevPostRunJourneyTests` uses an ephemeral debug port and isolated data. It runs the real column engine
with a fixture dispatcher and TypeSafe handler, enables post-run observation through the UI, verifies
that normal routing reaches Review while JEV suggests Todo, then checks for 32 seconds that no additional
execution or transition occurs. It uses the same `KITTYCLAW_JEV_DEBUG_E2E=1` opt-in as the initial journey.

## Human decisions with workflow effects

Both qualification and post-run proposals expose **Accept and apply destination** and **Reject and
requalify**. With the default manual policy, explicit owner acceptance applies the proposed
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
ticket shows the current decision and the full decision history. Human UI actions invoke the manual decision path; the observation worker can invoke the separately
checked automatic path only for columns that explicitly enable it.

The debug post-run browser journey now also accepts a proposal, verifies that the ordinary destination
agent runs, reloads the saved decision, then rejects another ticket's proposal and verifies that JEV
suggests an alternative using the supplied reason without launching another agent. All providers in
that journey are deterministic fixtures; real project data and production instances are not used.

## Optional automatic routing

In **Configure column → Processor**, enable **Apply JEV routes automatically** and set
**Minimum JEV confidence (%)**. The checkbox defaults to off for new and existing processors;
the initial threshold is 90%. Both qualification and post-run proposal modes support it. The
REST fields are `jevAutomaticRouting` and `jevConfidenceThreshold` (finite number from 0 to 1,
inclusive). Omitting either preserves its saved value. Both fields live in the authoritative
`processor.json` and its SQLite projection; automatic routing requires an enabled JEV mode.

After observing a ticket, `JevObservationEngine` attempts to apply an undecided proposal only
when the column opted in. `JevProposalDecisionService` uses the same atomic ticket/audit path
as human acceptance, with additional checks before and inside the transaction. Confidence must
meet or exceed the current threshold. `none`, errors, stale ticket/execution/configuration
snapshots, disabled processors, paused projects and already-decided revisions cannot route.
An active, retrying or child-waiting column execution prevents automatic routing. The same
automatic ticket/source/destination transition cannot repeat within ten minutes, which limits
qualification-only routing loops; manual acceptance remains available. Existing ticket-routing,
assignment and scheduling rules still apply. A successful move may start the destination processor.

A proposal includes a fingerprint of its source mission, instructions and business destinations.
Legacy proposals without this fingerprint remain available for manual review but cannot apply
automatically. Changing routing policy or its threshold does not require another inference;
changing the semantic routing context invalidates automatic application of the old result.
Below-threshold and ineligible proposals remain visible for manual acceptance or rejection.

Automatic decisions preserve the exact proposal and threshold with `automatic: true` and actor
`JEV`, sharing the transaction with the move. The ticket displays the current source-column
policy and distinguishes automatic application from owner decisions in both the current proposal
and the durable history. Duplicate sweeps cannot replay the same decision. Audit failure rolls
back the move and emits no status event.

`JevProposalDecisionTests` covers opt-in/defaults, inclusive boundaries, manual override below the
threshold, persistence with older clients, invalid values, stale/abstained/failed/rejected
proposals, pause/disable, active executions and repeated transitions. The opt-in
`JevAutomaticRoutingJourneyTests` uses isolated debug data and an ephemeral Kestrel port, real
Blazor UI, and fake JEV/agent providers: it verifies the manual default, enables automatic routing
with a 100% threshold in the column dialog, observes automatic application and the destination
agent, reloads the audit history, then proves an 80% proposal stays pending and can be rejected.
It uses `KITTYCLAW_JEV_DEBUG_E2E=1`; it never calls the live TypeSafe API.
