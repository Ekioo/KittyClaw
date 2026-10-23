namespace KittyClaw.Web.Services;

/// <summary>Shared temporal contract for workflow planning, application and project assistance.</summary>
internal static class ColumnWorkflowInstructions
{
    internal const string TemporalConfiguration = """
        ## Temporal column configuration contract

        Apply this contract whenever creating, refining, migrating or updating a pipeline.
        Read the live API documentation for request shapes. Explain and configure each column
        in the same temporal order as Configure column > Ticket path. Do not treat the options
        as independent toggles or assume every phase runs in every mode.

        Identity: name, color, role, pipelineId, board position (reorder index) and userGuidance describe the column; they
        do not select a processor mode. Use OwnerAction for human decisions and Waiting for
        non-human waits. Their userGuidance must explain the actual unblock action.

        1. ENTRY, before mode selection: maxTickets (0 means unlimited), capacityBlocking
           (warning versus refusal), requireAcceptanceCriteria and
           requireValidatedAcceptanceCriteria apply before a ticket can enter, in ALL modes.
           Validated entry requires every criterion already at PASS; it neither evaluates
           criteria nor creates evidence or grants owner approval. Never require PASS at entry
           to the very column expected to evaluate unevaluated criteria. Put that gate in a
           downstream column. Do not remove entry rules when disabling automatic processing.

        2. MODE, choose exactly one:
           - Do nothing more: no processor, or enabled=false for an existing processor.
             Preserve its stored configuration. Entry rules and independent schedules remain.
           - Work by an agent: enabled=true, jevObservationOnly=false,
             jevAcceptanceValidation=false. Optional JEV profile selection happens BEFORE
             dispatch; optional jevAutomaticRouting happens AFTER the saved agent result.
           - Automatic triage by JEV: enabled=true, jevObservationOnly=true,
             jevAcceptanceValidation=false, jevExecutionSelection=false,
             jevAutomaticRouting=false. The historical observation flag means active triage,
             not passive observability. With sufficient confidence JEV routes immediately,
             without executing the agent or its configured actions.
           - JEV evidence review, then owner decision: enabled=true,
             jevAcceptanceValidation=true, jevObservationOnly=false,
             jevExecutionSelection=false, jevAutomaticRouting=false. JEV examines existing
             evidence without running tests; a sufficiently certain review waits for the
             owner's decision, including when all criteria are satisfied. Configure
             jevAcceptanceCriteria, defaultTargetColumnId for approval and an outcome route
             changes_requested for corrections. Without that route rejection stays put.
           Do not enable legacy jevPostRunObservation alongside either JEV-only mode.

        3. PREPARE AGENT WORK: selectionOrder chooses an eligible ticket for an agent claim.
           jevExecutionSelection chooses from jevExecutionProfiles with explicit model,
           description and authorized skills. Required skills remain mandatory. If profile
           selection is unavailable or uncertain, use the column's default model and skills;
           this does not depend on the JEV-only fallback option. beforeActions are ordered
           preliminary actions; their execution can be deferred by success-route safeguards.

        4. EXECUTE AGENT WORK: processor name, mission, prompt, model (null means inherited),
           availableSkills, recommendedSkills, requiredSkills and maxTurns configure the run.
           They apply to the main Agent mode, or only to the opted-in fallback agent in a
           JEV-only mode. Do not imply an agent runs after successful JEV triage or review.

        5. SAVE RESULT AND EVIDENCE: evaluateAcceptanceCriteria requests assessments in the
           agent's final structured result, not on entry or by moving a ticket. PASS, FAIL and
           PARTIAL require non-empty evidence for the relevant existing criterion IDs.
           Unreported criteria stay unchanged. The platform records these assessments before
           routing, then capitalizes processor lessons. JEV review is a separate examination
           of existing evidence and never substitutes an agent claim for verified evidence.

        6. DECIDE THE DESTINATION: routes match the agent outcome, then
           defaultTargetColumnId handles other outcomes. jevAutomaticRouting can instead choose
           among those business destinations AFTER the agent result and evidence are saved.
           Unavailable or uncertain post-agent routing uses the classic outcome/default rules,
           not another agent run. jevConfidenceThreshold is 0..1 in API/files, 0..100 in the UI;
           it is shared by enabled JEV features. Technical and action-failure destinations
           are not JEV business candidates. Use stable destination IDs; do not route a column
           to itself. Declared destinations also constrain manual moves.

        7. FINALIZE: afterActions execute in configured order. Do not promise unconditional
           hook timing: beforeActions are deferred when a Success destination is declared.
           For a success outcome or accepted automatic JEV route, the platform validates and
           records the transition before pending configured actions; otherwise deferred
           beforeActions and afterActions run before the move. Already checkpointed actions
           are not replayed. Configure per-action failureTargetColumnId where needed.

        JEV-ONLY FAILURE BRANCH: explicitly choose jevFallbackToAgent for BOTH triage and
        evidence-review modes. Insufficient confidence/evidence, missing credentials, exhausted
        service budget and service errors follow the SAME policy. false keeps the ticket in
        place with a diagnostic and starts no agent; true uses the stored classic agent,
        actions and routing configuration. Only expose fallback agent settings when opted in.
        For NEW definitions explicitly send false unless the approved plan requests an agent
        fallback: omitted API/file values retain legacy true behavior. For updates preserve
        the saved policy unless the owner requests a change; do not silently re-enable it.
        JEV modes remain configurable without credentials. Successful reviews still require
        owner approval; confident unmet criteria are not an uncertainty fallback.

        TECHNICAL RECOVERY: maxAttempts, retryBackoffSeconds (initial exponential-backoff delay)
        and technicalFailureColumnId bound technical agent-processing retries and exhaustion.
        In a JEV-only mode with no agent fallback, do not claim they will start or retry an
        agent. Preserve hidden values rather than clearing them. An action may have its own
        failureTargetColumnId; follow the live action contract instead of treating it as a
        business outcome route.

        INDEPENDENT SCHEDULES: enabled, name, cron/calendar, timeZoneId, ticketScope and ordered
        actions belong to scheduled tasks, outside the ticket path. They run without an agent
        and are not disabled by Do nothing more or by changing a processor mode. Configure and
        verify them separately; do not convert a schedule into a per-ticket lifecycle phase.

        For pipeline kits, inspect the current portable format before assuming every option
        survives an import. The current portable processor carries agent settings and the
        fallback policy, but not JEV mode/profile/threshold configuration. Reapply an explicitly
        approved JEV configuration through native APIs or report the gap; do not claim an
        unchanged JEV lifecycle merely because a kit installed successfully.

        In a read-only plan, summarize this order and the mode/fallback choice in each column's
        description, naming unmet assumptions as risks; do not add unsupported JSON fields.
        During an approved application, inspect existing columns, processors and schedules,
        preserve stable IDs and unspecified/hidden settings, and explicitly retain enabled and
        JEV flags when an API save supplies defaults. Verify by reloading the saved settings:
        entry gates still apply in every mode, only the selected branch runs, evidence exists
        before a downstream PASS gate, fallback is opt-in where requested, and schedules remain
        independent. Do not launch live agents or make paid JEV calls merely to verify a plan.
        """;
}
