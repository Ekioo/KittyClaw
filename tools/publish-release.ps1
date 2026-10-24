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
    [string] $Configuration = 'Release',
    [switch] $KeepDraft,              # upload assets without publishing the release
    [string] $SignTemplate = $env:KITTYCLAW_SIGN_TEMPLATE
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
$releaseVersion = $Tag.TrimStart('v')
if (-not $IsWindows) { throw 'Release packaging requires Windows to build the Velopack installer.' }

# Seed a fresh staging directory with the previous full package so vpk can build a delta.
# This happens before creating/modifying any public release; downloads never publish anything.
$windowsOutput = Join-Path $repo ("publish-windows/release-" + [Guid]::NewGuid().ToString('N'))
$baselinePackage = & (Join-Path $PSScriptRoot 'Get-WindowsReleaseBaseline.ps1') `
    -Version $releaseVersion -OutputDir $windowsOutput
& (Join-Path $PSScriptRoot 'build-windows-installer.ps1') -Version $releaseVersion `
    -Configuration $Configuration -OutputDir $windowsOutput -SignTemplate $SignTemplate
if ($baselinePackage -and -not (Test-Path -LiteralPath (Join-Path $windowsOutput "Ekioo.KittyClaw-$releaseVersion-delta.nupkg"))) {
    throw 'A previous Windows package exists, but no incremental update was generated.'
}

# Each GitHub release owns its own full/delta assets. Older versions remain on their
# original releases and are combined by GithubSource when constructing an update chain.
$windowsFeedPath = Join-Path $windowsOutput 'releases.win.json'
$windowsFeed = Get-Content -LiteralPath $windowsFeedPath -Raw | ConvertFrom-Json
$windowsFeed.Assets = @($windowsFeed.Assets | Where-Object Version -eq $releaseVersion)
$windowsFeed | ConvertTo-Json -Depth 20 -Compress | Set-Content -LiteralPath $windowsFeedPath -Encoding utf8
$legacyFeedPath = Join-Path $windowsOutput 'RELEASES'
$currentPackagePattern = ' ' + [Regex]::Escape("Ekioo.KittyClaw-$releaseVersion-full.nupkg") + ' '
@(Get-Content -LiteralPath $legacyFeedPath | Where-Object { $_ -match $currentPackagePattern }) |
    Set-Content -LiteralPath $legacyFeedPath -Encoding utf8

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
$createdDraft = $false
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
    gh release create $Tag --title $Title --notes-file $notesFile --draft
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
    $createdDraft = $true
}
else {
    Write-Host "Release $Tag already exists; uploading release assets." -ForegroundColor DarkGray
}

$windowsAssets = @(Get-ChildItem -LiteralPath $windowsOutput -File | Where-Object {
    $_.Name -in 'Ekioo.KittyClaw-win-Setup.exe', "Ekioo.KittyClaw-$releaseVersion-full.nupkg",
        "Ekioo.KittyClaw-$releaseVersion-delta.nupkg", 'releases.win.json', 'RELEASES'
} | ForEach-Object FullName)
if (-not ($windowsAssets | Where-Object { $_ -like '*Setup.exe' }) -or
    -not (Test-Path -LiteralPath (Join-Path $windowsOutput 'releases.win.json'))) {
    throw 'Installer or update feed is missing.'
}
gh release upload $Tag $zip @windowsAssets --clobber
if ($LASTEXITCODE -ne 0) { throw 'gh release upload failed' }
if ($createdDraft -and -not $KeepDraft) {
    gh release edit $Tag --draft=false
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the prepared release.' }
}

if ($KeepDraft) {
    Write-Host "Draft ready: https://github.com/Ekioo/KittyClaw/releases/tag/$Tag" -ForegroundColor Green
} else {
    Write-Host "`nDone: https://github.com/Ekioo/KittyClaw/releases/tag/$Tag" -ForegroundColor Green
}
