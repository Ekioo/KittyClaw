#!/usr/bin/env pwsh
# Rebases a ticket worktree's branch onto dev, fast-forwards dev, then cleans up.
#
# Exit codes:
#   0 -merged and cleaned up
#   2 -main repo has uncommitted changes; aborted without touching anything
#   3 -worktree has uncommitted changes; aborted (committer must commit first)
#   4 -rebase onto dev produced conflicts; worktree left in a rebase with conflict markers
#       so a follow-up agent can resolve or abort it in the worktree itself
#   1 -any other failure
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [int] $TicketId
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repoParent = Split-Path $repo -Parent
$repoName = Split-Path $repo -Leaf
$wtPath = Join-Path (Join-Path $repoParent "$repoName.worktrees") "ticket-$TicketId"
$branch = "ticket/$TicketId"

function Log($msg) { Write-Host "[worktree-merge] $msg" -ForegroundColor DarkGray }
function Fail($code, $msg) { Write-Host "[worktree-merge] $msg" -ForegroundColor Yellow; exit $code }

if (-not (Test-Path $wtPath)) { Fail 1 "Worktree not found at $wtPath." }

$mainDirty = & git -C $repo status --porcelain
if ($mainDirty) {
    Fail 2 "Main repo has uncommitted changes. Refusing to merge to avoid clobbering owner work."
}

$wtDirty = & git -C $wtPath status --porcelain
if ($wtDirty) {
    Fail 3 "Worktree has uncommitted changes. Commit them first."
}

$wtBranch = (& git -C $wtPath rev-parse --abbrev-ref HEAD).Trim()
if ($wtBranch -ne $branch) {
    Fail 1 "Worktree HEAD is on '$wtBranch', expected '$branch'."
}

Log "Rebasing $branch onto dev (in worktree)."
& git -C $wtPath rebase dev
if ($LASTEXITCODE -ne 0) {
    Fail 4 "Conflicts rebasing $branch onto dev. Worktree retained in rebase state for recovery."
}

Log "Fast-forwarding dev to $branch."
& git -C $repo merge --ff-only $branch
if ($LASTEXITCODE -ne 0) {
    Fail 1 "git merge --ff-only failed unexpectedly."
}

Log "Removing worktree and branch."
& git -C $repo worktree remove --force $wtPath
if ($LASTEXITCODE -ne 0) { Log "worktree remove returned non-zero; continuing." }
& git -C $repo branch -d $branch
if ($LASTEXITCODE -ne 0) { Log "branch -d returned non-zero; check manually." }

Log "Done."
exit 0
