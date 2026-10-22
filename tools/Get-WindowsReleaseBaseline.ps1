# Retrieve the newest older stable Windows full package before building a delta.
# A missing baseline is valid for the first Velopack release; API/download failures are not.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutputDir,
    [string] $Repository = 'Ekioo/KittyClaw'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release baseline selection requires a stable version.' }
$metadata = & gh api "repos/$Repository/releases?per_page=100" --paginate --slurp
if ($LASTEXITCODE -ne 0) { throw 'Could not list GitHub releases for the Windows update baseline.' }
$pages = ($metadata -join "`n") | ConvertFrom-Json -Depth 30
$candidates = foreach ($page in $pages) {
    foreach ($release in $page) {
        if ($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^v(\d+\.\d+\.\d+)$') { continue }
        $previousVersion = $Matches[1]
        if ([Version]$previousVersion -ge [Version]$Version) { continue }
        $packageName = "Ekioo.KittyClaw-$previousVersion-full.nupkg"
        if ($packageName -notin $release.assets.name -or 'releases.win.json' -notin $release.assets.name) { continue }
        [PSCustomObject]@{ Version = [Version]$previousVersion; Tag = $release.tag_name; PackageName = $packageName }
    }
}
$baseline = $candidates | Sort-Object Version -Descending | Select-Object -First 1
if (-not $baseline) {
    Write-Host 'No older Velopack Windows release exists; this release will use full downloads.'
    return
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
& gh release download $baseline.Tag --repo $Repository --pattern $baseline.PackageName --dir $OutputDir --clobber
if ($LASTEXITCODE -ne 0) { throw "Could not download Windows baseline $($baseline.Tag)." }
$package = Join-Path $OutputDir $baseline.PackageName
if (-not (Test-Path -LiteralPath $package)) { throw 'The baseline download did not produce the expected package.' }
Write-Host "Windows delta baseline: $($baseline.Tag)"
return $package
