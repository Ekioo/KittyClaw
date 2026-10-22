# Deterministic release-selection tests. No GitHub requests or releases are made.
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'Get-WindowsReleaseBaseline.ps1'
$scratch = Join-Path $PSScriptRoot ("../publish-windows/baseline-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$global:KittyClawBaselineApiFailure = $false
$global:KittyClawBaselineDownloadFailure = $false
function New-TestRelease($version, $draft = $false, $pre = $false, $withPackage = $true) {
    @{ tag_name = "v$version"; draft = $draft; prerelease = $pre; assets = $(if ($withPackage) {
        @(@{name = "Ekioo.KittyClaw-$version-full.nupkg"}, @{name = 'releases.win.json'})
    } else { @() }) }
}
$global:KittyClawBaselineTestReleases = @((New-TestRelease '1.0.0'), (New-TestRelease '2.0.0'), (New-TestRelease '3.0.0'),
    (New-TestRelease '4.0.0'), (New-TestRelease '2.9.0' $false $true),
    (New-TestRelease '2.8.0' $true), (New-TestRelease '2.7.0' $false $false $false))
function gh {
    param([Parameter(ValueFromRemainingArguments)][object[]] $GitHubArgs)
    $global:LASTEXITCODE = 0
    if ($GitHubArgs[0] -eq 'api') {
        if ($global:KittyClawBaselineApiFailure) { $global:LASTEXITCODE = 1; return }
        ConvertTo-Json -InputObject @(@($global:KittyClawBaselineTestReleases)) -Depth 15 -Compress
    } else {
        if ($global:KittyClawBaselineDownloadFailure) { $global:LASTEXITCODE = 1; return }
        $pattern = [Array]::IndexOf($GitHubArgs, '--pattern')
        $directory = [Array]::IndexOf($GitHubArgs, '--dir')
        [IO.File]::WriteAllText((Join-Path $GitHubArgs[$directory + 1] $GitHubArgs[$pattern + 1]), 'synthetic package fixture')
    }
}
$selected = & $helper -Version '3.0.0' -OutputDir $scratch
if ((Split-Path $selected -Leaf) -ne 'Ekioo.KittyClaw-2.0.0-full.nupkg') { throw 'Incorrect prior stable release selected.' }
$saved = $global:KittyClawBaselineTestReleases
$global:KittyClawBaselineTestReleases = @((New-TestRelease '1.0.0' $false $false $false))
if (& $helper -Version '3.0.0' -OutputDir $scratch) { throw 'First Velopack release should not have a baseline.' }
$global:KittyClawBaselineTestReleases = $saved
$global:KittyClawBaselineApiFailure = $true
$failed = $false
try { & $helper -Version '3.0.0' -OutputDir $scratch } catch { $failed = $true }
if (-not $failed) { throw 'API failure was silently treated as a first release.' }
$global:KittyClawBaselineApiFailure = $false
$global:KittyClawBaselineDownloadFailure = $true
$failed = $false
try { & $helper -Version '3.0.0' -OutputDir $scratch } catch { $failed = $true }
if (-not $failed) { throw 'A failed baseline download must abort publication.' }
Write-Host 'Passed: previous stable selection, first release, API failure, download failure.' -ForegroundColor Green
