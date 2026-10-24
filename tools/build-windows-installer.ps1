# Builds local artifacts only. Publishing is owned by publish-release.ps1.
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $OutputDir,
    [string] $ReleaseNotes,
    [string] $SignTemplate
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Build the Windows installer on Windows.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) {
    $tag = git -C $repo describe --tags --exact-match 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Pass -Version for a local build, or build from a version tag.' }
    $Version = $tag.TrimStart('v')
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a SemVer version, such as 0.14.0.' }
if (-not $OutputDir) { $OutputDir = Join-Path $repo "publish-windows/$Version" }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
# Each invocation gets a fresh stage; never recursively delete a user-provided path.
$stage = Join-Path $repo ("publish-windows/stage-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Push-Location $repo
try {
    dotnet tool restore --add-source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned Velopack tool.' }
    dotnet publish KittyClaw.Web -c $Configuration -r win-x64 --self-contained true `
        -p:KittyClawWindowsInstaller=true "-p:MinVerVersionOverride=$Version" -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Windows self-contained publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'wwwroot/_framework/blazor.web.js'))) {
        throw 'Published Blazor bootstrap script is missing; refusing to package a non-interactive application.'
    }

    foreach ($runtimeFile in 'coreclr.dll', 'hostfxr.dll', 'Microsoft.AspNetCore.dll', 'System.Windows.Forms.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $runtimeFile))) { throw "Missing embedded runtime file: $runtimeFile" }
    }
    $packArgs = @('vpk', 'pack', '--packId', 'Ekioo.KittyClaw', '--packVersion', $Version,
        '--packDir', $stage, '--mainExe', 'KittyClaw.Web.exe', '--packTitle', 'KittyClaw',
        '--packAuthors', 'Ekioo', '--runtime', 'win-x64', '--channel', 'win', '--delta', 'BestSpeed',
        '--icon', (Join-Path $repo 'branding/KittyClaw.ico'), '--framework', 'webview2',
        '--shortcuts', 'Desktop,StartMenuRoot', '--outputDir', $OutputDir)
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path) }
    if ($SignTemplate) { $packArgs += @('--signTemplate', $SignTemplate) }
    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
    Write-Host "Windows installer and update feed: $OutputDir" -ForegroundColor Green
}
finally { Pop-Location }
