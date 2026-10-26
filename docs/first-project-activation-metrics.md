# First-project activation metrics

`GET /api/activation/first-project` returns the versioned, deduplicated first-project funnel. Events are correlated by `journeyId`; the earliest occurrence of each event name per journey wins, so retries and replays cannot inflate results.

The v1 funnel is `repository_selected` → `repository_validated` → `first_ticket_confirmed` → `minimal_workflow_ready` → `first_run_started` → `first_run_completed`. The response reports completion rate, median repository-to-completed-run minutes, abandonment counts at the last reached step, and the share of started journeys that opened project settings before their first completed run. Product targets are a median below 15 minutes and a settings-before-first-result rate below 0.20.

The query exposes only schema version, opaque journey correlation ID, event name, and timestamp. Repository paths, objectives, project/ticket identifiers, provider errors, and agent output are intentionally excluded.
