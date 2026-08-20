# Pipeline kits

## Purpose

Pipeline kits provide a portable `.kittyclaw-pipeline` ZIP format for exporting a sanitized pipeline and importing it into another project. Import analysis treats every archive as untrusted, validates its complete inventory without writing project state, and requires an explicit confirmation before an atomic installation.

## Key components

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

## External dependencies

- [Pipeline and column processing](./column-workflows.md) supplies the pipeline, column, processor, routing, and project-skill persistence services.
- [Project secrets vault](./project-secrets.md) resolves secret bindings without embedding secret values in a kit.
- `System.IO.Compression` reads and writes ZIP containers; SHA-256 protects the declared inventory.
