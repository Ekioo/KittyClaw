# Repository intake state contract

The first-project flow persists one state file per journey under the application data directory at `activation/repository-intake-{journeyId}.json`. The state contains the correlation ID, normalized repository path, retained objective, selection and validation timestamps, validation status, and the last stable error code.

The append-only `activation/repository-intake-events.jsonl` stream emits `repository_selected`, `repository_validated`, and `repository_intake_failed`. Every event includes the same journey ID and an UTC timestamp, allowing selection-to-validation latency to be calculated across retries. Downstream onboarding steps must consume only a state whose `isValidated` value is `true`.
