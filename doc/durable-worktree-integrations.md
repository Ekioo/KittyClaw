# Durable worktree integrations

## Purpose

When project worktrees are enabled, KittyClaw keeps the primary checkout read-only. Ticket work is written to the root ticket worktree; background durable writes such as memories, lessons, and dashboard outputs use a serialized maintenance worktree. Both kinds of work are integrated through a persistent queue, so a restart cannot silently forget pending changes. Disabling worktrees prevents new worktree creation but does not disable recovery and finalization of terminal-ticket worktrees that Git already has registered.

Before a maintenance execution starts, ignored `node_modules` directories found in the canonical workspace are exposed at the same relative paths in the maintenance worktree. Discovery excludes the complete sibling worktree container and all linked directories, so dependencies from maintenance or ticket worktrees cannot be rediscovered recursively. KittyClaw uses directory links (Windows junctions when symbolic-link privileges are unavailable), so dependencies are neither copied nor versioned and remain available when an existing worktree is reused after restart. Preparation failures stop the execution with an error that identifies both the dependency source and the intended worktree path.

The queue validates each rebased result in a dedicated per-project worktree whose HEAD is detached, then atomically advances `refs/kittyclaw/integration/<target>`. This integration tip is never checked out, so integration leaves the primary checkout, its target branch, index, tracked files, and untracked files unchanged even when that checkout is dirty. Synchronizing the checked-out target branch to the integration tip is a separate step. A target branch that diverges from the integration tip becomes `BlockedByExternalChanges`; the background reconciler can retry after reconciliation. A clean source worktree whose commit Git ancestry already proves integrated only needs cleanup. The queue also discovers registered worktrees for terminal root tickets after startup and queues them without creating missing worktrees. When the next pending job belongs to a ticket family that is still busy (active agent runs or processor writes), the queue skips it and integrates the next independent job instead of stalling behind it; the skipped job stays `Pending` and is retried later.

Before finalizing a terminal ticket, the queue waits until processor writes and ticket-family agent runs have stopped. Tracked changes and safe untracked project files are committed; recognized temporary files are removed. Local-only paths, sensitive paths, and probable secrets stop finalization in `NeedsReview` without staging, committing, or deleting the worktree. During a rebase, conflicts are union-merged and the rebase continues automatically only when every unresolved path is `.agents/processors/column-<number>/memory/MEMORY.md` or `pipeline-lessons.md`; both sides are retained. Any conflict involving another path remains entirely unresolved for manual review. Cleanup occurs only after Git ancestry proves that the final source commit is integrated.

An interrupted maintenance write is recovered automatically on the next reconciliation. If its worktree contains only safe changes, they are checkpointed with a recovery commit and the job is requeued; a clean worktree whose recorded commit is already an ancestor of the target is completed, otherwise it is requeued. The recovery is idempotent across repeated restarts. If the worktree contains local-only, sensitive, or probable-secret files, the next maintenance route resolution moves it aside to a `recovery/maintenance-<slug>-<suffix>` quarantine branch and worktree, records the row as `Quarantined` so it remains visible on the project tile, and creates a fresh maintenance worktree so new durable writes and interactive instructions continue without waiting for the review.

Versioned project knowledge stays in Git. Local control data such as prompts, transcripts, sessions, traces, `.env` files, and vault secrets is rejected from durable routes. Probable secrets stop the operation before its first commit.

## Key components

- `KittyClaw.Core/Services/DurableWriteRouter.cs` — selects ticket or maintenance worktrees, restricts writable paths, scans for probable secrets, commits maintenance changes, and checkpoints them in the queue.
- `KittyClaw.Core/Services/WorktreeMergeQueueService.cs` — persists integration jobs and their state in the project database, discovers terminal-ticket worktrees, classifies finalization files, commits validated durable changes, rebases source branches, validates them in detached integration worktrees, atomically publishes integration-tip refs, and preserves review or conflict states.
- `KittyClaw.Core/Services/WorktreeMergeQueueProcessor.cs` — periodically recovers terminal-ticket worktrees and reconciles pending or externally blocked jobs after startup or interruption.
- `KittyClaw.Core/Services/WorktreeFinalizationCoordinator.cs` — prevents finalization while a column processor is still writing to a ticket family worktree.
- `KittyClaw.Core/Automation/AgentMemoryHandler.cs` and `KittyClaw.Core/Services/ChatMemoryConsolidationService.cs` — route versioned memory writes away from the primary checkout.
- `KittyClaw.Core/Services/ColumnMemoryCapitalizationService.cs` — attributes processor lessons to the ticket worktree, or to maintenance work when no ticket exists.
- `KittyClaw.Core/Services/ColumnProcessorService.cs` — migrates legacy SQLite processor projections to versioned definitions through a maintenance worktree without dirtying the primary checkout.
- `KittyClaw.Core/Services/DashboardRefreshService.cs` — runs dashboard scripts and prompts in maintenance worktrees so generated file changes are isolated and reviewed.
- `KittyClaw.Web/Components/ProjectCards.razor` — displays live integration count, severity, and blocked age even for paused projects.

Queue states are `Pending`, `Processing`, `CommitPending`, `ValidationRequired`, `NeedsReview`, `BlockedByExternalChanges`, `Conflict`, `Failed`, `Quarantined`, and `Completed`. Worktrees, branches, commits, unexpected files, and conflict files remain in place until completion or an explicit recovery action; a `Quarantined` maintenance row keeps its moved worktree and quarantine branch for human review while a replacement maintenance worktree serves new writes.

## Entry points

- `GET /api/projects/{slug}/worktree-merges` lists the durable queue without exposing file contents.
- `POST /api/projects/{slug}/worktree-merges` enqueues a ticket worktree.
- `POST /api/projects/{slug}/worktree-merges/process-next` requests an immediate reconciliation.
- `POST /api/projects/{slug}/worktree-merges/{requestId}/resume` resumes a preserved review, conflict, failure, or external-change block after its cause is resolved.
- The project list polls queue summaries and presents warnings directly on project tiles.
- Memory consolidation, processor capitalization, dashboard migration, and dashboard refresh create maintenance jobs automatically.

## External dependencies

- Git worktrees, rebase, detached-HEAD fast-forward validation, and atomic ref updates.
- SQLite project databases for durable queue checkpoints.
- The project integration branch configured in project settings.
- ASP.NET Core hosted services for automatic reconciliation.
