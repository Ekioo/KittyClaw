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

foreach ($proj in 'KittyClaw.Web', 'KittyClaw.QaRunner', 'KittyClaw.ClaudeMock') {
    Write-Host "  -> $proj" -ForegroundColor DarkGray
    dotnet publish (Join-Path $repo $proj) -c $Configuration -o $Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

Write-Host "`nDone. Stable build is in $Out" -ForegroundColor Green
