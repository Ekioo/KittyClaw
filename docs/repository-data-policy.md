# Repository data policy

## Purpose

KittyClaw's published repository contains product source and deliberately
maintained synthetic fixtures. Execution evidence and exports may contain local
paths, private project data, credentials, or personal information and must never
be committed.

## Key components

- `tools/Test-RepositoryDataPolicy.ps1` scans tracked files and fails on
  prohibited evidence paths, board-export shapes, local user/worktree paths,
  non-synthetic email addresses, and common secret signatures.
- `.github/workflows/ci.yml` runs the scanner and its regression cases on every
  pull request and push to `main` or `dev`.
- `.githooks/pre-commit` provides the same guard locally after contributors set
  `core.hooksPath` to `.githooks`.
- `.githooks/pre-push` rejects branches whose ancestry reintroduces either
  purged Bloomii export blob.
- `.gitignore` excludes the entire `evidence/` tree.

Maintained fixtures must be minimal, synthetic, portable, and stored below a
test project's `Fixtures/` directory. Use reserved example domains and obvious
dummy identifiers. Never copy a production board or customer export into a
fixture.

## Entry points

- Run `pwsh ./tools/Test-RepositoryDataPolicy.ps1 -SelfTest` from the repository
  root.
- Enable the pre-commit hook with `git config core.hooksPath .githooks`.
- CI invokes the same command before compilation.

## External dependencies

- Git supplies the authoritative tracked-file inventory.
- PowerShell 7 runs the guard on Windows and Linux.

Historical removal of published private data is a coordinated incident action:
freeze integrations, create and verify an immutable mirror backup, rewrite all
affected refs in one pass, publish only if remote leases remain unchanged, and
require clone and branch resynchronization afterward.

After the 2026-08-17 purge, branches based on the previous graph must not be
merged or pushed. Recreate them from the current `origin/dev` and reapply only
their business changes. The scanner checks the complete `HEAD` ancestry for
the two forbidden Bloomii blob identifiers.
