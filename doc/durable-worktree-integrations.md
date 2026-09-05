# Durable worktree integrations

## Purpose

When project worktrees are enabled, KittyClaw keeps the primary checkout read-only. Ticket work is written to the root ticket worktree; background durable writes such as memories, lessons, and dashboard outputs use a serialized maintenance worktree. Both kinds of work are integrated through a persistent queue, so a restart cannot silently forget pending changes. Disabling worktrees prevents new worktree creation but does not disable recovery and finalization of terminal-ticket worktrees that Git already has registered.

Before a maintenance execution starts, ignored `node_modules` directories found in the canonical workspace are exposed at the same relative paths in the maintenance worktree. Discovery excludes the complete sibling worktree container and all linked directories, so dependencies from maintenance or ticket worktrees cannot be rediscovered recursively. KittyClaw uses directory links (Windows junctions when symbolic-link privileges are unavailable), so dependencies are neither copied nor versioned and remain available when an existing worktree is reused after restart. Preparation failures stop the execution with an error that identifies both the dependency source and the intended worktree path.

The queue validates each rebased result in a dedicated per-project worktree whose HEAD is detached, then atomically advances `refs/kittyclaw/integration/<target>`. This integration tip is never checked out, so integration leaves the primary checkout, its target branch, index, tracked files, and untracked files unchanged even when that checkout is dirty. Synchronizing the checked-out target branch to the integration tip is a separate durable step described below. A target branch that diverges from the integration tip becomes `BlockedByExternalChanges`; the background reconciler can retry after reconciliation. A clean source worktree whose commit Git ancestry already proves integrated only needs cleanup. The queue also discovers registered worktrees for terminal ticket families after startup — including legacy `ticket/<child>` worktrees whose root ticket is terminal — and queues them without creating missing worktrees; a `Completed` row whose registered worktree still exists is re-queued so finalization proves ancestry again before removing the worktree and its branch, and this recovery stays idempotent across repeated restarts. When the next pending job belongs to a ticket family that is still busy (active agent runs or processor writes), the queue skips it and integrates the next independent job instead of stalling behind it; the skipped job stays `Pending` and is retried later.

Before finalizing a terminal ticket, the queue waits until processor writes and ticket-family agent runs have stopped. Tracked changes and safe untracked project files are committed; recognized temporary files are removed. Local-only paths, sensitive paths, and probable secrets stop finalization in `NeedsReview` without staging, committing, or deleting the worktree. During a rebase, conflicts are union-merged and the rebase continues automatically only when every unresolved path is `.agents/processors/column-<number>/memory/MEMORY.md` or `pipeline-lessons.md`; both sides are retained. Any conflict involving another path remains entirely unresolved for manual review. Cleanup occurs only after Git ancestry proves that the final source commit is integrated.

An interrupted maintenance write is recovered automatically on the next reconciliation. If its worktree contains only safe changes, they are checkpointed with a recovery commit and the job is requeued; a clean worktree whose recorded commit is already an ancestor of the target is completed, otherwise it is requeued. The recovery is idempotent across repeated restarts. If the worktree contains local-only, sensitive, or probable-secret files, the next maintenance route resolution moves it aside to a `recovery/maintenance-<slug>-<suffix>` quarantine branch and worktree, records the row as `Quarantined` so it remains visible on the project tile, and creates a fresh maintenance worktree so new durable writes and interactive instructions continue without waiting for the review.

Each completed integration also records a durable local-checkout synchronization checkpoint on its queue row. The background processor reconciles it after every pass: a clean checkout on the target branch is fast-forwarded to the newest integrated commit; local tracked, staged, and untracked work is first captured in a recoverable backup ref (`refs/kittyclaw/sync-backups/<id>`) and re-applied with its index state after the fast-forward. A restore conflict, a divergent local commit, a checkout on another branch, a missing checkout, or a mutation detected by the before/after state snapshots leaves an explicit non-completed sync status with an actionable error while the integration itself stays acquired and later integrations keep flowing. Completion persists a `CleanupPending` checkpoint before deleting the backup ref, so a crash anywhere between the merge and the cleanup resumes idempotently; `RetrySynchronizationAsync` re-runs a conflicted, diverged, missing-checkout, or concurrent-changes row and re-applies the surviving backup when the checkout was reset clean.

Versioned project knowledge stays in Git. Local control data such as prompts, transcripts, sessions, traces, `.env` files, and vault secrets is rejected from durable routes. Probable secrets stop the operation before its first commit.

## Key components

- `KittyClaw.Core/Services/DurableWriteRouter.cs` — selects ticket or maintenance worktrees, restricts writable paths, scans for probable secrets, commits maintenance changes, and checkpoints them in the queue.
- `KittyClaw.Core/Services/WorktreeMergeQueueService.cs` — persists integration jobs and their state in the project database, discovers terminal-ticket worktrees, classifies finalization files, commits validated durable changes, rebases source branches, validates them in detached integration worktrees, atomically publishes integration-tip refs, and preserves review or conflict states.
- `KittyClaw.Core/Services/WorktreeMergeQueueProcessor.cs` — periodically recovers terminal-ticket worktrees, reconciles pending or externally blocked jobs after startup or interruption, and runs the pending local-checkout synchronization checkpoint after each pass.
- `KittyClaw.Core/Services/WorktreeFinalizationCoordinator.cs` — prevents finalization while a column processor is still writing to a ticket family worktree.
- `KittyClaw.Core/Automation/AgentMemoryHandler.cs` and `KittyClaw.Core/Services/ChatMemoryConsolidationService.cs` — route versioned memory writes away from the primary checkout.
- `KittyClaw.Core/Services/ColumnMemoryCapitalizationService.cs` — attributes processor lessons to the ticket worktree, or to maintenance work when no ticket exists.
- `KittyClaw.Core/Services/ColumnProcessorService.cs` — migrates legacy SQLite processor projections to versioned definitions through a maintenance worktree without dirtying the primary checkout.
- `KittyClaw.Core/Services/DashboardRefreshService.cs` — runs dashboard scripts and prompts in maintenance worktrees so generated file changes are isolated and reviewed.
- `KittyClaw.Web/Components/ProjectCards.razor` — displays live integration count, severity, and blocked age even for paused projects.

Queue states are `Pending`, `Processing`, `CommitPending`, `ValidationRequired`, `NeedsReview`, `BlockedByExternalChanges`, `Conflict`, `Failed`, `Quarantined`, and `Completed`. Each row additionally tracks a local-checkout synchronization status (`NotRequired`, `Pending`, `Processing`, `CleanupPending`, `Completed`, `Conflict`, `Diverged`, `CheckoutMissing`, `ConcurrentChanges`) with its target commit, backup ref, error, and conflict files. Worktrees, branches, commits, unexpected files, and conflict files remain in place until completion or an explicit recovery action; a `Quarantined` maintenance row keeps its moved worktree and quarantine branch for human review while a replacement maintenance worktree serves new writes.

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
