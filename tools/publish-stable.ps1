#!/usr/bin/env pwsh
# Publishes the three runnable KittyClaw projects (Web + QaRunner + ClaudeMock)
# into a single sibling-exe layout, which is what the qa-tester skill and the
# QaRunner's TestInstance expect (KITTYCLAW_QARUNNER_EXE / KittyClaw.ClaudeMock.exe
# resolved relative to KittyClaw.Web.exe).
[CmdletBinding()]
param(
    [string] $Out = 'C:\KittyClaw-stable',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

Write-Host "Publishing KittyClaw ($Configuration) to $Out ..." -ForegroundColor Cyan

# Web + QaRunner: published as siblings (KITTYCLAW_QARUNNER_EXE expects this layout).
foreach ($proj in 'KittyClaw.Web', 'KittyClaw.QaRunner') {
    Write-Host "  -> $proj" -ForegroundColor DarkGray
    dotnet publish (Join-Path $repo $proj) -c $Configuration -o $Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# ClaudeMock: published into a qa-mock/ subfolder so it does NOT sit next to KittyClaw.Web.exe
# as `claude.exe`. Otherwise ClaudeRunner.ResolveClaudeBinary() would prefer the mock for *all*
# agents, not just QA. The QaRunner's TestInstance picks it up explicitly via KITTYCLAW_CLAUDE_BIN.
$mockOut = Join-Path $Out 'qa-mock'
Write-Host "  -> KittyClaw.ClaudeMock (-> $mockOut)" -ForegroundColor DarkGray
dotnet publish (Join-Path $repo 'KittyClaw.ClaudeMock') -c $Configuration -o $mockOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for KittyClaw.ClaudeMock" }

Write-Host "`nDone. Stable build is in $Out" -ForegroundColor Green
