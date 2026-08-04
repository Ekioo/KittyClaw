#!/usr/bin/env pwsh
# Publishes the three runnable KittyClaw projects (Web + QaRunner + ClaudeMock)
# into a single sibling-exe layout, which is what the qa-tester skill and the
# QaRunner's TestInstance expect (KITTYCLAW_QARUNNER_EXE / KittyClaw.ClaudeMock.exe
# resolved relative to KittyClaw.Web.exe).
#
# Versioning: the assembly version is derived automatically by MinVer from the
# latest `vX.Y.Z` git tag (full release ritual: see RELEASING.md at the repo
# root). No manual edits to any csproj are required. Builds between tags are emitted
# as pre-releases (e.g. 0.7.1-alpha.0.3). MinVer needs full git history, so
# avoid `git clone --depth 1` when invoking this script.
[CmdletBinding()]
param(
    [string] $Out = 'C:\KittyClaw-stable',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$resolvedOut = [IO.Path]::GetFullPath($Out)
$publishedWebRoot = [IO.Path]::GetFullPath((Join-Path $resolvedOut 'wwwroot'))
if (-not $publishedWebRoot.StartsWith($resolvedOut.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a published web root outside '$resolvedOut'."
}

Write-Host "Publishing KittyClaw ($Configuration) to $Out ..." -ForegroundColor Cyan

# dotnet publish skips copying a content file when the destination happens to have a newer
# timestamp. That can pair a new static-assets manifest with stale JS/CSS and make Kestrel
# truncate the old file to the new byte length. Remove only the validated published asset
# directory; logs and sibling executables remain untouched.
if (Test-Path -LiteralPath $publishedWebRoot -PathType Container) {
    Remove-Item -LiteralPath $publishedWebRoot -Recurse -Force
}

# Web + QaRunner: published as siblings (KITTYCLAW_QARUNNER_EXE expects this layout).
foreach ($proj in 'KittyClaw.Web', 'KittyClaw.QaRunner') {
    Write-Host "  -> $proj" -ForegroundColor DarkGray
    # Static-web-asset manifests include content lengths and fingerprints. Incremental
    # publish can retain a stale manifest after a JS/CSS edit, causing Kestrel to serve a
    # truncated asset even though the copied file is complete. Force those manifests to be
    # rebuilt for every stable publication.
    if ($proj -eq 'KittyClaw.Web') {
        dotnet clean (Join-Path $repo $proj) -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed for $proj" }
    }
    dotnet publish (Join-Path $repo $proj) -c $Configuration -o $Out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# ClaudeMock: published into a qa-mock/ subfolder so it does NOT sit next to KittyClaw.Web.exe
# as `claude.exe`. Otherwise AgentRunner.ResolveClaudeBinary() would prefer the mock for *all*
# agents, not just QA. The QaRunner's TestInstance picks it up explicitly via KITTYCLAW_CLAUDE_BIN.
$mockOut = Join-Path $Out 'qa-mock'
Write-Host "  -> KittyClaw.ClaudeMock (-> $mockOut)" -ForegroundColor DarkGray
dotnet publish (Join-Path $repo 'KittyClaw.ClaudeMock') -c $Configuration -o $mockOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for KittyClaw.ClaudeMock" }

Write-Host "`nDone. Stable build is in $Out" -ForegroundColor Green
