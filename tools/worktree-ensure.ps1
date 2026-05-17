#!/usr/bin/env pwsh
# Ensures a per-ticket git worktree exists for the given ticket id.
# Idempotent: prints the absolute worktree path on stdout regardless of pre-state.
# Convention: branch `ticket/<id>`, folder `<repo>.worktrees/ticket-<id>`, based on local `main`.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [int] $TicketId
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoParent = Split-Path $repo -Parent
$repoName = Split-Path $repo -Leaf
$wtRoot = Join-Path $repoParent "$repoName.worktrees"
$wtPath = Join-Path $wtRoot "ticket-$TicketId"
$branch = "ticket/$TicketId"

function Log($msg) { Write-Host "[worktree-ensure] $msg" -ForegroundColor DarkGray }

if (-not (Test-Path $wtRoot)) {
    New-Item -ItemType Directory -Path $wtRoot -Force | Out-Null
}

$wtList = & git -C $repo worktree list --porcelain
$registered = $false
if ($wtList) {
    $normalized = $wtPath.Replace('\','/').TrimEnd('/')
    foreach ($line in $wtList) {
        if ($line -like 'worktree *') {
            $p = $line.Substring(9).Replace('\','/').TrimEnd('/')
            if ($p -ieq $normalized) { $registered = $true; break }
        }
    }
}

if ($registered) {
    Log "Worktree already registered."
    Write-Output $wtPath
    exit 0
}

$folderExists = Test-Path $wtPath
$branchExists = $null -ne (& git -C $repo rev-parse --verify --quiet "refs/heads/$branch" 2>$null)

if ($folderExists -and -not $branchExists) {
    Log "Folder exists but no branch and not registered - attempting repair."
    & git -C $repo worktree repair $wtPath 2>&1 | Out-Null
    $wtList2 = & git -C $repo worktree list --porcelain
    $normalized = $wtPath.Replace('\','/').TrimEnd('/')
    foreach ($line in $wtList2) {
        if ($line -like 'worktree *') {
            $p = $line.Substring(9).Replace('\','/').TrimEnd('/')
            if ($p -ieq $normalized) { Write-Output $wtPath; exit 0 }
        }
    }
    Write-Error "Orphan folder at $wtPath cannot be repaired. Remove it manually and retry."
    exit 1
}

if ($folderExists -and $branchExists) {
    Log "Folder + branch exist, attaching worktree."
    & git -C $repo worktree add --force $wtPath $branch
    if ($LASTEXITCODE -ne 0) { Write-Error "git worktree add failed."; exit 1 }
    Write-Output $wtPath
    exit 0
}

if ($branchExists) {
    Log "Branch $branch exists, creating worktree from it."
    & git -C $repo worktree add $wtPath $branch
} else {
    Log "Creating new branch $branch from local main."
    & git -C $repo worktree add $wtPath -b $branch main
}
if ($LASTEXITCODE -ne 0) { Write-Error "git worktree add failed."; exit 1 }

Write-Output $wtPath
exit 0
