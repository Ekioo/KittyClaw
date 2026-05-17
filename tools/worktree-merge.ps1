#!/usr/bin/env pwsh
# Fast-forward merges a ticket worktree's branch into main, then cleans up.
#
# Exit codes:
#   0 -merged and cleaned up
#   2 -main repo has uncommitted changes; aborted without touching anything
#   3 -worktree has uncommitted changes; aborted (committer must commit first)
#   4 -merge into ticket branch produced conflicts; worktree left with conflict markers
#       so a follow-up agent can resolve them in the worktree itself
#   1 -any other failure
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [int] $TicketId
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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

Log "Merging main into $branch (in worktree)."
& git -C $wtPath merge main --no-edit
if ($LASTEXITCODE -ne 0) {
    Fail 4 "Conflicts merging main into $branch. Worktree retained with conflict markers."
}

Log "Fast-forwarding main to $branch."
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
