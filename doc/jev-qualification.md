# JEV qualification in observation mode

## Purpose

A column processor can opt into `JevObservationOnly`. This replaces normal agent processing
for that column with a read-only routing suggestion. Agent dispatch, before/after actions,
column execution claims, and automatic ticket moves are bypassed. Independent scheduled tasks
and legacy automations retain their own configuration.

## Configuration

In **Configure column → Processor**, enable **JEV qualification — observation only**.
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
cannot leave a seemingly valid routing proposal. Question version `qualification-v2` invalidates
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

Confidence describes the answer distribution, not a guarantee of correctness. No threshold can
activate routing in this implementation. No live TypeSafe call is needed by the test suite:
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
