#!/usr/bin/env pwsh
# Cuts the GitHub release for the vX.Y.Z tag on HEAD: builds the release zip
# (same sibling-exe layout as publish-stable.ps1: Web + QaRunner at the root,
# ClaudeMock in qa-mock/), creates the GitHub release from the matching
# CHANGELOG.md entry when it doesn't exist yet, and uploads the zip as
# KittyClaw-vX.Y.Z.zip.
#
# Full release ritual: see RELEASING.md at the repo root.
[CmdletBinding()]
param(
    [string] $Tag,                    # defaults to the exact vX.Y.Z tag on HEAD
    [string] $Title,                  # defaults to "vX.Y — <changelog one-line summary>"
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

# --- Preflight -------------------------------------------------------------
$branch = git -C $repo rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { throw "Release builds must come from main (current branch: $branch)." }
if (git -C $repo status --porcelain) { throw 'Working tree is not clean; commit or stash first.' }
if (-not $Tag) {
    $Tag = git -C $repo describe --tags --exact-match 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'HEAD is not tagged. Tag first (git tag vX.Y.Z && git push origin vX.Y.Z) or pass -Tag.' }
}
if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)$') { throw "Tag '$Tag' does not match vX.Y.Z." }
$shortVersion = "v$($Matches[1]).$($Matches[2])"   # CHANGELOG headings use vX.Y

# --- Build the zip ---------------------------------------------------------
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "kittyclaw-release-$Tag"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "Building release zip for $Tag ..." -ForegroundColor Cyan
foreach ($proj in 'KittyClaw.Web', 'KittyClaw.QaRunner') {
    Write-Host "  -> $proj" -ForegroundColor DarkGray
    dotnet publish (Join-Path $repo $proj) -c $Configuration -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}
Write-Host "  -> KittyClaw.ClaudeMock (-> qa-mock/)" -ForegroundColor DarkGray
dotnet publish (Join-Path $repo 'KittyClaw.ClaudeMock') -c $Configuration -o (Join-Path $stage 'qa-mock')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for KittyClaw.ClaudeMock' }

# MinVer must agree with the tag we are shipping under (guards against building
# from a stale checkout or an untagged commit passed via -Tag).
$built = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'KittyClaw.Web.dll')).ProductVersion
if (-not $built.StartsWith($Tag.TrimStart('v'))) { throw "Built version '$built' does not match tag $Tag." }

$zip = Join-Path ([System.IO.Path]::GetTempPath()) "KittyClaw-$Tag.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("Zip ready: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor DarkGray

# --- Create the release from the CHANGELOG entry (when missing) ------------
gh release view $Tag *> $null
if ($LASTEXITCODE -ne 0) {
    $changelog = Get-Content (Join-Path $repo 'CHANGELOG.md') -Raw
    $pattern = "(?ms)^## \[$([regex]::Escape($shortVersion))\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)"
    if ($changelog -notmatch $pattern) { throw "No '## [$shortVersion]' entry found in CHANGELOG.md." }
    # Release bodies use ## headings where the CHANGELOG uses ###; drop the trailing --- separator.
    $notes = ($Matches[1] -replace '(?m)^### ', '## ').Trim() -replace '---\s*$', ''
    $notes = $notes.Trim()
    if (-not $Title) {
        $summary = ($notes -split '\r?\n' | Where-Object { $_ } | Select-Object -First 1).TrimEnd('.')
        $Title = "$shortVersion — $summary"
    }
    $notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "kittyclaw-release-notes-$Tag.md"
    Set-Content $notesFile $notes
    Write-Host "Creating GitHub release $Tag ..." -ForegroundColor Cyan
    gh release create $Tag --title $Title --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
else {
    Write-Host "Release $Tag already exists; uploading the zip only." -ForegroundColor DarkGray
}

gh release upload $Tag $zip --clobber
if ($LASTEXITCODE -ne 0) { throw 'gh release upload failed' }

Write-Host "`nDone: https://github.com/Ekioo/KittyClaw/releases/tag/$Tag" -ForegroundColor Green
