# JEV qualification in observation mode

## Purpose

A column processor can opt into `JevObservationOnly`. This replaces normal agent processing
for that column with a read-only routing suggestion. Agent dispatch, before/after actions,
column execution claims, and automatic ticket moves are bypassed. Independent scheduled tasks
and legacy automations retain their own configuration.

## Configuration

In **Configure column → Processor**, enable **JEV qualification — observation only**.
Set the mission and optional instructions, and configure outcome routes or a default destination
in the Routing tab. Add `TYPESAFE_API_KEY` through the project's existing write-only secret vault.
The REST processor contract also accepts `jevObservationOnly`; omitting it preserves the saved mode.
The flag is versioned in the authoritative `processor.json` and projected into SQLite.

Ticket title, description, priority and the latest twenty comments are sent to TypeSafe, together
with the column mission, instructions and candidate destination names/outcomes. Contexts above
48,000 characters are rejected locally. Only explicitly configured business routes/defaults are
candidates; technical/action failure routes are excluded. `none` is always available.

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
and replaces only the TypeSafe HTTP client with a deterministic fixture. The project
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
