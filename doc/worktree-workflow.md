# Per-ticket worktree workflow

## Status

Each project persists a `WorktreesEnabled` flag, an integration branch, and optionally a `RepositoryPath` distinct from its control `WorkspacePath`. New projects default to worktrees enabled; migrated projects keep the historical disabled value so an upgrade never changes their execution directory unexpectedly. Worktrees can still be disabled explicitly in project settings. When enabled and configured, ticket-bound agent runs automatically use the canonical worktree of the root ticket.

Disable this pattern only when a project intentionally needs every agent to share one checkout. Worktrees require a usable Git repository and an existing local integration branch before ticket-bound execution begins.

## What ships in the product

These pieces are in the repo and available to every project:

- Project registry settings — `WorktreesEnabled`, `IntegrationBranch`, `RepositoryPath`, and the read-only `ResolvedRepositoryPath` are exposed by `GET /api/projects/{slug}`. A relative repository path is resolved from the control workspace and persisted as an absolute path. Enabling validates the exact configured Git root, worktree support, and the named local integration branch before persisting the change.
- `TicketWorktreeService` — follows the ticket parent chain, then creates or reuses `<repo>.worktrees/ticket-<root-id>` on branch `ticket/<root-id>`. It returns both the canonical worktree and primary repository paths. Missing, unregistered, or branch-inconsistent worktrees fail resolution before a provider starts.
- `AgentRunner` — keeps loading skills and `.agents/**` from the control workspace while launching the provider process in the resolved worktree. The resolved directory is retained across provider fallback and steering replay, persisted on the run, and exposed by the runs API as `workingDirectory`. Runs sharing a worktree are serialized; runs for different roots may execute concurrently. For a worktree-bound run, the runner fingerprints tracked changes and untracked file contents in the primary repository before and after execution. Orchestrator-owned `.agents/channel/**` state and consolidated agent memory are excluded; any other primary-repository change fails the run.
- `WorktreeMergeQueueService` — persists integration requests per project, processes them in creation order, and serializes rebase plus fast-forward operations. It records dirty-checkout failures and rebase conflicts without deleting the ticket worktree, supports explicit resume, and recovers interrupted processing after restart.
- `tools/worktree-ensure.ps1` — idempotent. Creates a worktree from local `main` if absent, or returns the path of the existing one. Convention: branch `ticket/<N>`, folder `<repo>.worktrees/ticket-<N>`. Usage: `powershell.exe -NoProfile -File tools/worktree-ensure.ps1 <N>`; the absolute path is printed on the last stdout line.
- `tools/worktree-merge.ps1` — rebases the local unpublished ticket branch onto `dev`, fast-forwards `dev` to it, then removes the worktree and deletes the branch. This keeps ticket integration linear without merge commits.
- `{ticketId}` placeholder support in `concurrencyGroup` and `mutuallyExclusiveWith` (see [automation engine](./automation-engine.md)). Lets you serialize agents per-ticket without serializing across tickets.

### `worktree-merge.ps1` exit codes

| Code | Meaning |
|------|---------|
| 0    | Merged and cleaned up |
| 1    | Other failure (worktree missing, FF rejected unexpectedly, …) |
| 2    | Main repo has uncommitted changes — aborted without touching anything |
| 3    | Worktree has uncommitted changes — commit first, then retry |
| 4    | Conflict rebasing the ticket branch onto `dev` — the worktree is left in rebase state so a follow-up agent can resolve it or run `git rebase --abort` |

## Initializing a missing repository

If the configured workspace exists but contains no Git metadata, project settings shows an **Initialize a Git repository** action (`data-testid="git-init"`). After confirmation it calls `POST /api/projects/{slug}/git/init`, backed by `KittyClaw.Core/Services/GitRepositoryInitializationService.cs`: the path comes exclusively from the project configuration, only `git init` is executed (no commit, no remote), existing files stay untracked, and existing repositories — including a parent repository detected via `rev-parse --show-toplevel` or a `.git` file — are rejected without modification. `GET /api/projects/{slug}/git` exposes the same detection as a `GitRepositoryStatus`. A first commit on the integration branch is still required before worktrees can be enabled.

## How to enable it for a project

Open the project settings, enter the code repository (absolute or relative to the control workspace), enable **Git worktrees**, enter an existing local integration branch, and save. The form shows the effectively resolved repository and validates its exact Git root, worktree support, and branch before activation. The same settings remain available through `PATCH /api/projects/{slug}` with `repositoryPath`, `worktreesEnabled: true`, and `integrationBranch`.

For compatibility, projects with no `RepositoryPath` keep the historical workspace-based Git resolution; migration never guesses a nested repository or changes the target. Configure `RepositoryPath` explicitly to opt into a distinct or nested code repository. Control files (`.agents/**`, memories, skills, automations, and dashboard files) always remain rooted at `WorkspacePath`.

When enabled, each ticket drawer shows the canonical root ticket, path and branch shared by the whole ticket family. It also shows worktree cleanliness and merge-queue state/position, with actionable guidance for waiting, dirty checkouts, conflicts, failures and successful integration. Safe retry/resume actions are available for pending or failed requests. When disabled, this panel is absent and the normal ticket flow is unchanged.

## Caveats

- **Live host serves `main`, not the worktree.** The orchestrator (`KITTYCLAW_WEB_EXE`) runs the published stable, which reflects `main`. Agents that need to test their changes (`qa-tester` typically) must build the worktree themselves and pass the resulting binary to `KittyClaw.QaRunner --web-exe …`, not rely on `${KITTYCLAW_API_URL}` for verification.
- **`.agents/` is not copied into worktrees.** That is intentional: preamble + SKILL are injected into the prompt by the orchestrator (sourced from the primary `<workspace>/.agents/`), and memory writes belong in that single location so they survive `git worktree remove`.
- **`bin/` and `obj/` must stay gitignored** so a worktree build does not show up in `git status` and get swept into the ticket commit.
- **`git worktree remove --force`** is used by `worktree-merge.ps1` because Debug build artifacts (untracked) would otherwise block the cleanup.

## Entry points

- `TicketWorktreeService.ResolveAsync` — resolves the root ticket and prepares its canonical worktree before a run starts.
- `GET /api/projects/{slug}/worktree-merges` — lists durable queue state and integration results.
- `POST /api/projects/{slug}/worktree-merges` — idempotently enqueues a ticket family for integration.
- `POST /api/projects/{slug}/worktree-merges/process-next` — processes the oldest pending request.
- `POST /api/projects/{slug}/worktree-merges/{requestId}/resume` — resumes a failed or conflict-paused request after correction.
- `AgentRunner.RunAsync` — selects the execution directory and applies the per-worktree execution gate.
- `tools/worktree-ensure.ps1`, `tools/worktree-merge.ps1` — explicit helpers for implementation and integration workflows.
- `KittyClaw.Core/Automation/ActionExecutor.cs` and `RunStateManager.cs` — perform the `{ticketId}` substitution in `concurrencyGroup` and `mutuallyExclusiveWith`.

## External dependencies

- `git worktree` — standard git feature; git must be on `PATH`.
- [Agent dispatch](./agent-dispatch.md) — launches provider processes in the resolved execution directory.
- [Automation engine](./automation-engine.md) — supplies ticket-bound runs and optional higher-level concurrency groups.
