# Pipeline kits

## Purpose

Pipeline kits provide a portable `.kittyclaw-pipeline` ZIP format for exporting a sanitized pipeline and importing it into another project. Import analysis treats every archive as untrusted, validates its complete inventory without writing project state, and requires an explicit confirmation before an atomic installation.

## Key components

Creating a project detects root-level `.kittyclaw-pipeline` files and asks whether to import them, including when guided workflow setup is skipped. The confirmation lists the detected filenames before project creation. Declining continues without importing; accepting opens the standard import review for each detected kit. The project exists at that point, but no pipeline is installed until its review is confirmed. Owners can grant each executable approval, resolve conflicts and bindings, import disabled, or skip the kit. Files are processed in filename order and retained. Duplicate pipeline names receive a numeric suffix; identical skills are reused, while differing skill collisions are reported without overwriting. Invalid archives do not prevent other kits from importing. Missing parameters, prerequisites, secrets or executable approvals retain the existing disabled-install behavior. Import failures and activation blockers are shown before onboarding continues.

- `KittyClaw.Core/Services/PipelineExportService.cs` builds sanitized kits with a versioned manifest and SHA-256 inventory.
- `KittyClaw.Core/Services/PipelineKitScanner.cs` identifies content that must not be exported.
- `KittyClaw.Web/Components/PipelineKitDialog.razor` presents the reviewed export and two-phase import workflow, including collision remapping, masked vault bindings, separate executable-capability approvals, and activation blockers.
- `KittyClaw.Core/Services/PipelineImportService.cs` rejects hostile or altered archives, previews conflicts and prerequisites, remaps approved skill collisions, and rolls back partial installation failures.
- `KittyClaw.Core/Models/PipelineKit.cs` and `PipelineKitImport.cs` define the portable format, preview, confirmation, and result contracts.

## Entry points

- `GET /api/projects/{slug}/pipelines/{pipelineId}/export` downloads a sanitized kit.
- `POST /api/projects/{slug}/pipeline-kits/analyze` validates a kit and returns its inventory, hashes, creation plan, collisions, missing inputs, and required approvals without writing files or project data.
- `POST /api/projects/{slug}/pipeline-kits/confirm` accepts the kit plus the reviewed confirmation as multipart form data and creates the pipeline and embedded skills atomically.
- The project **Workflows** page opens the pipeline-kit dialog for the selected pipeline. Export remains unavailable until scanner review succeeds; import analyzes the selected local file without writes and requires an explicit confirmation before installation.

Unknown format versions, path traversal, links, executable binaries, nested archives, duplicate paths, hash mismatches, and documented archive-limit violations are rejected before installation. Name and skill collisions are resolved by explicit rename or identical-content reuse; existing content is never overwritten. Embedded scripts, `executePowerShell`, and `httpRequest` each require owner approval, and imported processors remain disabled while an input, vault binding, prerequisite, or approval is missing. Kit content is never executed during analysis or installation. URL-based import is not supported.

A kit that references project skills requires ordinary project skill roots.
Existing skill directories that contain symbolic links or reparse points are
ineligible for identical-content reuse. Choose a new slug for an embedded skill
when its original slug is occupied by such a directory.

## Temporal configuration and portable options

Review a pipeline kit in the same [temporal option order](column-workflows.md#temporal-column-editor)
as a native column: entry requirements → mode → agent preparation/execution → assessments
and evidence → routing/finalization, with recovery and independent schedules treated separately.
A successful import does not prove that every source option is represented in the format.

The current portable column preserves capacity and both criterion-entry gates. Its processor
preserves enabled state, mission/prompt/model/skills, agent limits/retries, ordered actions,
routing, `evaluateAcceptanceCriteria` and all JEV behavior settings:

- `jevObservationOnly` (triage), `jevAutomaticRouting`, `jevPostRunObservation`,
  `jevAcceptanceValidation`, `jevExecutionSelection` and `jevFallbackToAgent`.
- `jevConfidenceThreshold` (0–1, default 0.9), `jevAcceptanceCriteria` and
  `jevExecutionProfiles` (name, model, skills, description). Profile skill references
  participate in prerequisites, embedded skill export and collision remapping.
  Profile skills already belong to the configured column skills, so their embedded
  folders remain recognizable to older readers.
- Destinations use `routing.default`, `routing.technicalFailure`, `routing.routes[].target`
  and action `onFailure`, all resolved using column keys within the newly imported
  pipeline, independently of display names, pipeline renames or an existing main pipeline.

These are additive optional fields in format v1: old kits retain their defaults and old
readers ignore unknown fields. No version bump is necessary. Pipeline records have no
separate JEV options. Runtime timestamps/history and credentials are excluded;
`jevPostRunObservation` restores the mode with a fresh local activation timestamp.
Import analysis displays the JEV settings and reports missing TypeSafe credentials with a
link to project secrets. Add `TYPESAFE_API_KEY` and reanalyze before confirmation, or
explicitly enable processors after adding credentials to an already disabled import.
Existing global/environment credentials are recognized using the runtime resolver.

Independent scheduled tasks preserve name, enabled state, cron, time zone, ticket scope
and ordered actions, with failure destinations remapped to imported columns. IDs are
regenerated and history is excluded. Missing approvals or prerequisites disable imported
schedules and processors. Older kits without schedules remain supported.

Do not move an all-`PASS` entry requirement into the column that must first evaluate the
criteria. Do not infer an agent fallback from a missing key or a successful kit installation.
Read back each supported setting and preserve existing policies during later native updates.

## Ticket creation portability

Each portable `createTicket` action carries `createTarget`, a logical key of a column
inside the exported pipeline. It applies to preparation/finalization actions and independent
scheduled tasks. Import validates the key during write-free analysis and resolves it against
the imported columns; the key takes precedence over the legacy `action.status` display name.
The installed action receives the resolved column name expected by the native executor,
which creates tickets within the current ticket's (or scheduled task's) pipeline.
`action.status` remains in exports so older v1 readers can import ordinary kits.
Older kits without `createTarget` are accepted only when `status` identifies an existing
column inside the kit. Unknown creation destinations are rejected before installation;
self-targeting creation is allowed. These optional fields retain format v1.

An explicit `createTicket.parentId` is a project-local ticket identifier: exports are blocked
until it is removed, and untrusted imports containing it are rejected before any write.
Ticket records are not bundled and parent identifiers must never be reused in a new project.

## External dependencies

- [Pipeline and column processing](./column-workflows.md) supplies the pipeline, column, processor, routing, and project-skill persistence services.
- [Project secrets vault](./project-secrets.md) resolves secret bindings without embedding secret values in a kit.
- `System.IO.Compression` reads and writes ZIP containers; SHA-256 protects the declared inventory.
