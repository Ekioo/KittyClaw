# Per-ticket worktree workflow (opt-in)

## Status

**Opt-in pattern.** Each project persists a `WorktreesEnabled` flag and an integration branch, but the flag defaults to disabled for both new and migrated projects. When enabled, ticket-bound agent runs automatically use the canonical worktree of the root ticket. A freshly initialized KittyClaw project keeps all agents working in the single project workspace.

Adopt this pattern only if you need filesystem isolation between concurrent agentic work on different tickets (e.g. several programmers in flight simultaneously, or a desire to keep `main` clean while work is in progress).

## What ships in the product

These pieces are in the repo and available to every project:

- Project registry settings — `WorktreesEnabled` and `IntegrationBranch` are exposed by `GET /api/projects/{slug}` and can be changed independently through `PATCH /api/projects/{slug}`. Enabling validates that the workspace is a usable Git repository, that Git supports worktrees, and that the named local integration branch exists before persisting the change.
- `TicketWorktreeService` — follows the ticket parent chain, then creates or reuses `<repo>.worktrees/ticket-<root-id>` on branch `ticket/<root-id>`. Git failures are reported in the run without cleaning or changing the primary checkout.
- `AgentRunner` — keeps loading skills and `.agents/**` from the control workspace while launching the provider process in the resolved worktree. Runs sharing a worktree are serialized; runs for different roots may execute concurrently.
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

## How to enable it for a project

Set `worktreesEnabled: true` and a valid local `integrationBranch` through `PATCH /api/projects/{slug}`. No agent-skill or automation changes are required for ticket-bound runs. Runs without a ticket keep using the control workspace.

## Caveats

- **Live host serves `main`, not the worktree.** The orchestrator (`KITTYCLAW_WEB_EXE`) runs the published stable, which reflects `main`. Agents that need to test their changes (`qa-tester` typically) must build the worktree themselves and pass the resulting binary to `KittyClaw.QaRunner --web-exe …`, not rely on `${KITTYCLAW_API_URL}` for verification.
- **`.agents/` is not copied into worktrees.** That is intentional: preamble + SKILL are injected into the prompt by the orchestrator (sourced from the primary `<workspace>/.agents/`), and memory writes belong in that single location so they survive `git worktree remove`.
- **`bin/` and `obj/` must stay gitignored** so a worktree build does not show up in `git status` and get swept into the ticket commit.
- **`git worktree remove --force`** is used by `worktree-merge.ps1` because Debug build artifacts (untracked) would otherwise block the cleanup.

## Entry points

- `TicketWorktreeService.ResolveAsync` — resolves the root ticket and prepares its canonical worktree before a run starts.
- `AgentRunner.RunAsync` — selects the execution directory and applies the per-worktree execution gate.
- `tools/worktree-ensure.ps1`, `tools/worktree-merge.ps1` — explicit helpers for implementation and integration workflows.
- `KittyClaw.Core/Automation/ActionExecutor.cs` and `RunStateManager.cs` — perform the `{ticketId}` substitution in `concurrencyGroup` and `mutuallyExclusiveWith`.

## External dependencies

- `git worktree` — standard git feature; git must be on `PATH`.
- [Agent dispatch](./agent-dispatch.md) — launches provider processes in the resolved execution directory.
- [Automation engine](./automation-engine.md) — supplies ticket-bound runs and optional higher-level concurrency groups.
