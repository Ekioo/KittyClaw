# Hermetic publication-gate tests: no network, native build or release mutation.
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'Get-MacOSReleaseArtifacts.ps1'
$scratch = Join-Path $PSScriptRoot ("../publish-macos/release-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$global:MacReleaseTestMode = 'success'
$global:MacReleasePolls = 0
$global:MacReleaseSleeps = 0
$global:MacReleaseDownloads = 0
$commit = 'a' * 40
function gh {
    param([Parameter(ValueFromRemainingArguments)][object[]] $GitHubArgs)
    $global:LASTEXITCODE = 0
    if ($GitHubArgs[0] -ne 'run') { throw 'The artifact helper must never mutate a release.' }
    if ($GitHubArgs[1] -eq 'list') {
        $global:MacReleasePolls++
        if ($global:MacReleaseTestMode -eq 'api-failure') { $global:LASTEXITCODE = 1; return }
        $status = if ($global:MacReleaseTestMode -eq 'queued' -and $global:MacReleasePolls -eq 1) { 'queued' } else { 'completed' }
        $conclusion = if ($global:MacReleaseTestMode -eq 'failed-run') { 'failure' } else { 'success' }
        $expected = @{ databaseId = 123; headSha = ('a' * 40); headBranch = 'v1.2.3'; status = $status; conclusion = $conclusion }
        if ($global:MacReleaseTestMode -eq 'wrong-sha') { $expected.headSha = 'b' * 40 }
        if ($global:MacReleaseTestMode -eq 'wrong-tag') { $expected.headBranch = 'v1.2.4' }
        # Unrelated newer branch builds must never be mistaken for release artifacts.
        ConvertTo-Json -InputObject @(@{ databaseId = 999; headSha = ('a' * 40); headBranch = 'dev'; status = 'completed'; conclusion = 'success' }, $expected) -Compress
        return
    }
    if ($GitHubArgs[1] -ne 'download' -or $GitHubArgs[2] -ne 123) { throw 'Unexpected build selected for download.' }
    $global:MacReleaseDownloads++
    $directory = $GitHubArgs[[Array]::IndexOf($GitHubArgs, '--dir') + 1]
    $runtime = if ($directory.EndsWith('osx-arm64')) { 'osx-arm64' } else { 'osx-x64' }
    if ($global:MacReleaseTestMode -eq 'missing-architecture' -and $runtime -eq 'osx-x64') { $global:LASTEXITCODE = 1; return }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'INSTALL.txt'), 'Experimental, ad-hoc signed and not notarized.')
    $packageId = "Ekioo.KittyClaw.$runtime.Experimental"
    $filename = "$packageId-1.2.3-$runtime-experimental-full.nupkg"
    $packagePath = Join-Path $directory $filename
    [IO.File]::WriteAllText($packagePath, 'synthetic macOS package')
    $asset = @{ PackageId = $packageId; Version = '1.2.3'; Type = 'Full'; FileName = $filename;
        Size = (Get-Item $packagePath).Length; SHA256 = (Get-FileHash $packagePath -Algorithm SHA256).Hash }
    switch ($global:MacReleaseTestMode) {
        'wrong-version' { $asset.Version = '0.0.2-ci.123' }
        'wrong-package' { $asset.PackageId = 'Ekioo.KittyClaw.osx-other.Experimental' }
        'corrupt-package' { [IO.File]::WriteAllText($packagePath, 'tampered bytes') }
        'wrong-hash' { $asset.SHA256 = '0' * 64 }
        'path-traversal' { $asset.FileName = '../outside.nupkg' }
    }
    @{ Assets = @($asset) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $directory "releases.$runtime-experimental.json")
    foreach ($suffix in 'Setup.pkg', 'Portable.zip') {
        if ($global:MacReleaseTestMode -eq 'missing-installer' -and $suffix -eq 'Setup.pkg') { continue }
        [IO.File]::WriteAllText((Join-Path $directory "$packageId-$runtime-experimental-$suffix"), 'synthetic installer/archive')
    }
}
function Start-Sleep {
    param([int] $Seconds)
    $global:MacReleaseSleeps++
    if ($global:MacReleaseTestMode -in 'wrong-sha', 'wrong-tag') { throw 'No matching release run; stopped test polling.' }
    if ($global:MacReleaseSleeps -gt 2) { throw 'Unexpected polling loop.' }
}
foreach ($mode in 'success', 'queued', 'failed-run', 'api-failure', 'wrong-sha', 'wrong-tag',
    'missing-architecture', 'wrong-version', 'wrong-package', 'corrupt-package', 'wrong-hash', 'path-traversal', 'missing-installer') {
    $global:MacReleaseTestMode = $mode
    $global:MacReleasePolls = 0
    $global:MacReleaseSleeps = 0
    $global:MacReleaseDownloads = 0
    $accepted = [Collections.Generic.List[string]]::new()
    $failed = $false
    try { & $helper -Tag v1.2.3 -Commit $commit -OutputDir (Join-Path $scratch $mode) | ForEach-Object { $accepted.Add($_) } }
    catch { $failed = $true }
    if ($mode -in 'success', 'queued') {
        if ($failed -or $accepted.Count -ne 9 -or $global:MacReleaseDownloads -ne 2) { throw "Valid release artifacts were rejected: $mode" }
        if ($mode -eq 'queued' -and $global:MacReleaseSleeps -ne 1) { throw 'Pending native validation was not awaited.' }
    } else {
        if (-not $failed -or $accepted.Count -ne 0) { throw "Invalid assets reached publication: $mode" }
        if ($mode -in 'failed-run', 'api-failure', 'wrong-sha', 'wrong-tag' -and $global:MacReleaseDownloads -ne 0) { throw "Downloaded an unverified build: $mode" }
    }
    Write-Host "Passed: $mode"
}
