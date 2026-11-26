# Wait for the exact tag's native CI validation and retrieve publishable assets.
# This helper never creates, modifies or publishes a release.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $Commit,
    [Parameter(Mandatory)][string] $OutputDir,
    [string] $Repository = 'Ekioo/KittyClaw',
    [int] $TimeoutMinutes = 60
)
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw 'macOS release artifacts require a stable vX.Y.Z tag.' }
$version = $Matches[1]
if ($Commit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Pass the exact tagged commit SHA.' }
if ($TimeoutMinutes -lt 1) { throw 'TimeoutMinutes must be positive.' }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
$run = $null
do {
    $metadata = & gh run list --repo $Repository --workflow macos-installer.yml --commit $Commit `
        --event push --limit 100 --json databaseId,headSha,headBranch,status,conclusion
    if ($LASTEXITCODE -ne 0) { throw 'Could not query macOS release builds.' }
    $run = @(($metadata -join "`n") | ConvertFrom-Json) | Where-Object {
        $_.headSha -eq $Commit -and $_.headBranch -eq $Tag
    } | Select-Object -First 1
    if ($run -and $run.status -eq 'completed') {
        if ($run.conclusion -ne 'success') { throw "macOS validation failed for $Tag (run $($run.databaseId), $($run.conclusion))." }
        break
    }
    if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for macOS validation of $Tag. Push the tag and inspect macos-installer.yml." }
    Write-Host "Waiting for both native macOS builds for $Tag ..."
    Start-Sleep -Seconds 15
} while ($true)

$assets = [Collections.Generic.List[string]]::new()
foreach ($runtime in 'osx-arm64', 'osx-x64') {
    $directory = Join-Path $OutputDir $runtime
    if (Test-Path -LiteralPath $directory) { throw 'Use a fresh output directory for release artifact downloads.' }
    & gh run download $run.databaseId --repo $Repository --name "KittyClaw-macOS-experimental-$runtime" --dir $directory | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not download verified $runtime artifacts." }
    $feedPath = Join-Path $directory "releases.$runtime-experimental.json"
    $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
    $packageId = "Ekioo.KittyClaw.$runtime.Experimental"
    $full = @($feed.Assets | Where-Object Type -eq 'Full')
    if ($full.Count -ne 1 -or $full[0].Version -cne $version -or $full[0].PackageId -cne $packageId) {
        throw "The $runtime feed does not describe the exact tagged release $version."
    }
    # Never resolve an untrusted feed path outside the downloaded artifact.
    $filename = $full[0].FileName
    if ($filename -cne "$packageId-$version-$runtime-experimental-full.nupkg") { throw 'Unexpected macOS update package filename.' }
    $package = Get-Item -LiteralPath (Join-Path $directory $filename)
    if ($package.Length -ne $full[0].Size -or -not $full[0].SHA256 -or
        (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash -ine $full[0].SHA256) {
        throw "The $runtime update package failed size/hash validation."
    }
    foreach ($path in @($feedPath, $package.FullName,
        (Join-Path $directory "$packageId-$runtime-experimental-Setup.pkg"),
        (Join-Path $directory "$packageId-$runtime-experimental-Portable.zip"))) {
        $file = Get-Item -LiteralPath $path
        if ($file.Length -eq 0) { throw "Empty macOS release artifact: $path" }
        $assets.Add($file.FullName)
    }
}
# Provide the unsigned-installation instructions once, without colliding between architectures.
$readme = Join-Path $OutputDir 'KittyClaw-macOS-INSTALL.txt'
Copy-Item -LiteralPath (Join-Path $OutputDir 'osx-arm64/INSTALL.txt') -Destination $readme
$assets.Add((Get-Item -LiteralPath $readme).FullName)
# Both downloads have passed every gate before returning anything for publication.
Write-Host "Verified macOS assets for $Tag from run $($run.databaseId)."
return $assets.ToArray()
