# Activation test protocol

## Goal

Measure whether at least 60% of qualified trial users start a first agent run within ten minutes, and whether the five-minute journey communicates KittyClaw's value.

## Cohort and success rule

A **qualified trial user** is a participant who has a software repository or realistic sample project, can run KittyClaw locally, and has a supported agent backend configured before the timed session. Exclude sessions with an unavailable backend, installation failure, or facilitator intervention before the timer starts; record exclusions and their reason separately.

Start the timer when the participant first sees a usable KittyClaw board. Stop it at the earliest recorded `run_started` event for a ticket in that project.

The primary activation metric is:

```text
qualified users with first_run_seconds <= 600 / all qualified users
```

The acceptance threshold is **at least 60%**. Report the numerator, denominator, percentage, median time to first run, and every exclusion. Do not round a result below 60% up to the threshold.

## Session protocol

1. Give the participant the checkout ticket from the [five-minute demo](product-journey-demo.md), but do not tell them which controls to use.
2. Record `board_ready` when the board becomes usable.
3. Let the participant create or open the ticket, assign it, and start work.
4. Record `run_started` when KittyClaw creates the first run and associates it with the ticket.
5. Let the participant inspect the board, run, and evidence, then make a release decision.
6. Ask, “What is KittyClaw for?” and record the answer verbatim.
7. Record whether the participant independently identified the live board, readable run, and human release decision.

Facilitators may resolve installation problems before `board_ready`. After that event, any hint is an intervention and the session remains in the denominator.

## Instrumentation record

Use a local session log or research worksheet with one record per participant. Do not include repository content, ticket descriptions, run output, names, email addresses, or machine identifiers.

| Field | Type | Meaning |
|---|---|---|
| `session_id` | random string | Session-scoped identifier with no user identity |
| `cohort` | string | Trial source or study round |
| `board_ready_at` | UTC timestamp | Timer start |
| `first_run_at` | UTC timestamp or null | Earliest run start in the project |
| `first_run_seconds` | integer or null | Difference from `board_ready_at` |
| `activated_10m` | boolean | `first_run_seconds <= 600` |
| `intervention_count` | integer | Hints after timer start |
| `three_proofs_recalled` | integer, 0–3 | Proofs named without prompting |
| `value_statement` | string | Verbatim answer to the value question |
| `excluded_reason` | string or null | Pre-timer qualification failure only |

KittyClaw's anonymous daily heartbeat is not suitable for this study because it intentionally contains no behavioral data. Keep activation-study records local unless participants separately consent to another collection method.

## Analysis and decision

Analyze a minimum pilot cohort of five qualified users to detect obvious journey failures; use at least ten before treating the percentage as directional product evidence. Segment failures by the first incomplete step: project ready, ticket created, assignee chosen, or run started.

The journey passes when activation is at least 60% and a majority of activated participants describe KittyClaw in substance as a local way to control agent-performed software work with visible progress and a human release decision. If activation misses the threshold, revise the earliest failing step and repeat with a new cohort rather than combining results across materially different journeys.
