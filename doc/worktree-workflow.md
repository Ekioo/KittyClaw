# Per-ticket worktree workflow (opt-in)

## Status

**Opt-in pattern.** The helper scripts and the automation placeholder shipped here exist so a project *can* adopt per-ticket worktrees, but `ProjectTemplate/Agents/**` does **not** enable it by default. A freshly initialized KittyClaw project keeps all agents working in the single project workspace.

Adopt this pattern only if you need filesystem isolation between concurrent agentic work on different tickets (e.g. several programmers in flight simultaneously, or a desire to keep `main` clean while work is in progress).

## What ships in the product

These pieces are in the repo and available to every project:

- `tools/worktree-ensure.ps1` — idempotent. Creates a worktree from local `main` if absent, or returns the path of the existing one. Convention: branch `ticket/<N>`, folder `<repo>.worktrees/ticket-<N>`. Usage: `powershell.exe -NoProfile -File tools/worktree-ensure.ps1 <N>`; the absolute path is printed on the last stdout line.
- `tools/worktree-merge.ps1` — fast-forwards `main` to the ticket branch in the worktree, then removes the worktree and deletes the branch.
- `{ticketId}` placeholder support in `concurrencyGroup` and `mutuallyExclusiveWith` (see [automation engine](./automation-engine.md)). Lets you serialize agents per-ticket without serializing across tickets.

### `worktree-merge.ps1` exit codes

| Code | Meaning |
|------|---------|
| 0    | Merged and cleaned up |
| 1    | Other failure (worktree missing, FF rejected unexpectedly, …) |
| 2    | Main repo has uncommitted changes — aborted without touching anything |
| 3    | Worktree has uncommitted changes — commit first, then retry |
| 4    | Conflict merging `main` into the ticket branch — the worktree is left with conflict markers so a follow-up agent can resolve them |

## What is NOT in the product

- The agent **preamble** and **committer SKILL** in `ProjectTemplate/Agents/` do not invoke these scripts. Out of the box, agents share a single workspace.
- Other agent SKILLs (`programmer`, `qa-tester`, …) likewise have no worktree awareness in the template.

## How to enable it for a project

You need three things in the project's `<workspace>/.agents/`:

1. **Preamble** — add a "Per-ticket worktrees" section that tells every agent: if your prompt contains `Focus on ticket #N`, resolve `$wt = $(powershell.exe -NoProfile -File tools/worktree-ensure.ps1 N | tail -n 1)` and operate exclusively under `$wt` for that run. Memory/skill files stay in `<workspace>/.agents/` (do not copy them into the worktree).
2. **Committer SKILL** — at the end of a ticket, commit any pending worktree changes with `git -C "$wt" commit`, then run `tools/worktree-merge.ps1 N`. Branch on the exit code: 0 → success comment; 2 → defer with comment; 4 → move ticket back to `Todo` with a comment naming the worktree path. Never `git checkout main` from the worktree (main is checked out in the primary repo).
3. **Automations** — for every ticket-bound `runAgent` action, set `concurrencyGroup: "ticket-{ticketId}"`. On the committer action, keep a global `git` concurrency group **and** add `mutuallyExclusiveWith: ["ticket-{ticketId}"]` so the merge waits for any worker in the same worktree.

## Caveats

- **Live host serves `main`, not the worktree.** The orchestrator (`KITTYCLAW_WEB_EXE`) runs the published stable, which reflects `main`. Agents that need to test their changes (`qa-tester` typically) must build the worktree themselves and pass the resulting binary to `KittyClaw.QaRunner --web-exe …`, not rely on `${KITTYCLAW_API_URL}` for verification.
- **`.agents/` is not copied into worktrees.** That is intentional: preamble + SKILL are injected into the prompt by the orchestrator (sourced from the primary `<workspace>/.agents/`), and memory writes belong in that single location so they survive `git worktree remove`.
- **`bin/` and `obj/` must stay gitignored** so a worktree build does not show up in `git status` and get swept into the ticket commit.
- **`git worktree remove --force`** is used by `worktree-merge.ps1` because Debug build artifacts (untracked) would otherwise block the cleanup.

## Entry points

- `tools/worktree-ensure.ps1`, `tools/worktree-merge.ps1` — invoked by whichever agent SKILLs the project chooses to wire them into.
- `KittyClaw.Core/Automation/ActionExecutor.cs` and `RunStateManager.cs` — perform the `{ticketId}` substitution in `concurrencyGroup` and `mutuallyExclusiveWith`.

## External dependencies

- `git worktree` — standard git feature; git must be on `PATH`.
- [Automation engine](./automation-engine.md) — the placeholder substitution is what prevents two agents from racing in the same worktree.
