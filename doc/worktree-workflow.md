# Per-ticket worktree workflow

## Purpose
Isolates each ticket's code changes in a dedicated git worktree so multiple agents can work concurrently on different tickets without interfering with each other or with the main branch.

## Key components
- `tools/worktree-ensure.ps1` — idempotent: creates the worktree from local `main` if absent, or returns the path of the existing one. Convention: branch `ticket/<N>`, folder `<repo>.worktrees/ticket-<N>`.
- `tools/worktree-merge.ps1` — fast-forwards `main` to the ticket branch and removes the worktree. Distinct exit codes signal: main dirty (1), worktree dirty (2), merge conflict (3).

## Entry points
- Called by the [committer agent](../ProjectTemplate/Agents/committer/) at the start and end of a ticket run.
- Documented in the agent preamble (`CLAUDE.md` system prompt) under **Per-ticket worktrees**.

## External dependencies
- `git worktree` — standard git feature; git must be on PATH.
- [Automation engine](./automation-engine.md) — `{ticketId}` placeholder in `concurrencyGroup` / `mutuallyExclusiveWith` prevents two agents from entering the same worktree simultaneously.
