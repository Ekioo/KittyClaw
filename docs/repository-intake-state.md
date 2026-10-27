# Repository intake state contract

When project creation explicitly requests Git initialization, intake checks the workspace path and write access, initializes an eligible folder with `git init`, then validates the resulting repository. Without that option, a non-Git folder remains rejected. Initialization does not stage files or create a commit.

`ProjectCreationJourneyTests` exercises the real browser flow with an isolated server and mock provider: selecting an empty folder through Browse, creating Git metadata, editing and confirming the first ticket, opening the board and reloading it. It also covers an existing repository and a rejected attempt without Git consent followed by a successful retry. The tests run with the normal test suite (including CI); Chromium must be installed. To run only these journeys, use `dotnet test KittyClaw.Core.Tests --filter FullyQualifiedName~ProjectCreationJourneyTests`.

The first-project flow persists one state file per journey under the application data directory at `activation/repository-intake-{journeyId}.json`. The state contains the correlation ID, normalized repository path, retained objective, selection and validation timestamps, validation status, and the last stable error code.

The append-only `activation/repository-intake-events.jsonl` stream emits `repository_selected`, `repository_validated`, and `repository_intake_failed`. Every event includes the same journey ID and an UTC timestamp, allowing selection-to-validation latency to be calculated across retries. Downstream onboarding steps must consume only a state whose `isValidated` value is `true`.
