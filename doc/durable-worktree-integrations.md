# Durable worktree integrations

## Purpose

When project worktrees are enabled, KittyClaw keeps the primary checkout read-only. Ticket work is written to the root ticket worktree; background durable writes such as memories, lessons, and dashboard outputs use a serialized maintenance worktree. Both kinds of work are integrated through a persistent queue, so a restart cannot silently forget pending changes.

The queue never modifies a target branch while its primary checkout has local changes. Isolated runs continue, while the integration becomes `BlockedByExternalChanges` and remains visible on the project tile. The background reconciler retries it after the external changes are resolved.

Versioned project knowledge stays in Git. Local control data such as prompts, transcripts, sessions, traces, `.env` files, and vault secrets is rejected from durable routes. Declared paths are staged explicitly, and probable secrets stop the operation before its first commit.

## Key components

- `KittyClaw.Core/Services/DurableWriteRouter.cs` — selects ticket or maintenance worktrees, restricts writable paths, scans for probable secrets, commits maintenance changes, and checkpoints them in the queue.
- `KittyClaw.Core/Services/WorktreeMergeQueueService.cs` — persists integration jobs and their state in the project database, rebases source branches, fast-forwards the target, preserves conflicts, and classifies dirty target checkouts.
- `KittyClaw.Core/Services/WorktreeMergeQueueProcessor.cs` — periodically reconciles pending and externally blocked jobs after startup or interruption.
- `KittyClaw.Core/Automation/AgentMemoryHandler.cs` and `KittyClaw.Core/Services/ChatMemoryConsolidationService.cs` — route versioned memory writes away from the primary checkout.
- `KittyClaw.Core/Services/ColumnMemoryCapitalizationService.cs` — attributes processor lessons to the ticket worktree, or to maintenance work when no ticket exists.
- `KittyClaw.Core/Services/DashboardRefreshService.cs` — runs dashboard scripts and prompts in maintenance worktrees so generated file changes are isolated and reviewed.
- `KittyClaw.Web/Components/ProjectCards.razor` — displays live integration count, severity, and blocked age even for paused projects.

Queue states are `Pending`, `Processing`, `CommitPending`, `ValidationRequired`, `NeedsReview`, `BlockedByExternalChanges`, `Conflict`, `Failed`, and `Completed`. Worktrees, branches, commits, unexpected files, and conflict files remain in place until completion or an explicit recovery action.

## Entry points

- `GET /api/projects/{slug}/worktree-merges` lists the durable queue without exposing file contents.
- `POST /api/projects/{slug}/worktree-merges` enqueues a ticket worktree.
- `POST /api/projects/{slug}/worktree-merges/process-next` requests an immediate reconciliation.
- `POST /api/projects/{slug}/worktree-merges/{requestId}/resume` resumes a preserved conflict, failure, or external-change block.
- The project list polls queue summaries and presents warnings directly on project tiles.
- Memory consolidation, processor capitalization, dashboard migration, and dashboard refresh create maintenance jobs automatically.

## External dependencies

- Git worktrees, rebase, and fast-forward merge operations.
- SQLite project databases for durable queue checkpoints.
- The project integration branch configured in project settings.
- ASP.NET Core hosted services for automatic reconciliation.
