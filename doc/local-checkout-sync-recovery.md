# Local checkout synchronization recovery

## Purpose

Integration and local synchronization are deliberately independent. Once a queue request is `Completed`, its integrated commit is durable on the integration tip even if the configured local checkout cannot yet move to that commit. The ticket panel and worktree-merge API therefore report both phases separately.

## Migration from earlier versions

Existing queue rows are migrated in place when first read. Rows created before synchronization checkpoints existed retain `NotRequired`; new completed integrations record a target commit and enter the synchronization state machine. No repository rewrite is required. After upgrading, allow the background processor to run once, then inspect `GET /api/projects/{slug}/worktree-merges` for non-completed synchronization states.

Before recovery, record the request id, `integratedCommit`, `syncTargetCommit`, `hasSynchronizationLag`, `syncBackupRef`, `syncError`, and `syncConflictFiles`. Do not delete a `refs/kittyclaw/sync-backups/<id>` ref while its request is incomplete: it is the durable pointer to local work captured before the checkout advanced.

## Recovery procedure

### Checkout is behind but otherwise clean

Use **Retry local synchronization** in the ticket panel, or call `POST /api/projects/{slug}/worktree-merges/{requestId}/retry-synchronization`. The retry advances only the local checkout; it never repeats or changes the completed integration. `Pending`, `Processing`, and `CleanupPending` checkpoints are resumed automatically and do not expose a manual retry button.

### Restore conflict

The original local work remains reachable through `syncBackupRef`, and `syncConflictFiles` identifies the affected paths. Choose one of these approaches:

1. Resolve the files in the configured checkout, stage the intended result, and retry. If the checkout already points at `syncTargetCommit`, KittyClaw accepts the resolved index and completes without re-applying the backup.
2. To discard the conflicted attempt but preserve the backup, reset the checkout and working tree to `syncTargetCommit`, then retry. KittyClaw re-applies the surviving backup and restores its staged/unstaged state.

Review the resulting files before deleting anything manually. KittyClaw removes the backup ref only after persisting the `CleanupPending` checkpoint and completing synchronization.

### Diverged checkout or wrong branch

Preserve any local commits, then reconcile the configured checkout with the project integration branch and `syncTargetCommit`. A checkout on another branch is treated as divergence because KittyClaw will not switch branches implicitly. Once the intended target branch is checked out and its history can be safely advanced, retry synchronization.

### Concurrent local changes

Stop or finish the other Git/file operation reported by `syncError`. Verify the checkout has not changed again, then retry. Snapshot checks prevent KittyClaw from overwriting changes that appeared during synchronization.

### Configured checkout is absent

Restore the directory at the configured repository path, or update project settings to the correct existing Git checkout. Confirm that it is the exact repository root and has the configured integration branch checked out. Retry afterward; the integrated commit remains safe while the folder is absent.

## Verification

A recovered request shows integration `Completed`, synchronization `Completed`, and `syncTargetCommit` equal to the local checkout's `HEAD`. `syncBackupRef`, `syncError`, and `syncConflictFiles` are then empty. If retry returns HTTP 409, refresh the request: its state is no longer manually retryable and may already be processing or complete.

## Key components

- `WorktreeMergeQueueService.SynchronizeNextAsync` performs checkpointed local synchronization.
- `WorktreeMergeQueueService.RetrySynchronizationAsync` accepts only recoverable terminal synchronization states.
- `TicketPanel.razor` exposes lag, diagnostics, commits, and retry without conflating them with integration.

## External dependencies

- Git refs and checkout state in the configured repository.
- The durable per-project SQLite queue.
- [Durable worktree integrations](./durable-worktree-integrations.md) for queue semantics and state definitions.
