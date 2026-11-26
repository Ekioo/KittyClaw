# Experimental, ad-hoc signed artifacts only: no Apple account or publication.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][ValidateSet('osx-arm64', 'osx-x64')][string] $Runtime,
    [string] $Configuration = 'Release',
    [string] $OutputDir
)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'Build macOS packages on a macOS host.' }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Pass a SemVer version.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$channel = "$Runtime-experimental"
if (-not $OutputDir) { $OutputDir = Join-Path $repo "publish-macos/$Version/$Runtime" }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$scratch = Join-Path $repo ("publish-macos/stage-" + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $scratch 'payload'
$iconset = Join-Path $scratch 'KittyClaw.iconset'
New-Item -ItemType Directory -Path $stage, $iconset, $OutputDir -Force | Out-Null

Push-Location $repo
try {
    dotnet tool restore --add-source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned Velopack tool.' }
    dotnet publish KittyClaw.Web -c $Configuration -r $Runtime --self-contained true `
        -p:KittyClawMacOSInstaller=true "-p:MinVerVersionOverride=$Version" -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'macOS self-contained publish failed.' }
    foreach ($file in 'KittyClaw.Web', 'libcoreclr.dylib', 'libhostfxr.dylib',
        'Microsoft.AspNetCore.dll', 'wwwroot/_framework/blazor.web.js') {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Missing published file: $file" }
    }

    # Reuse the established pictogram; iconutil and sips ship with macOS.
    foreach ($size in 16, 32, 128, 256, 512) {
        foreach ($scale in 1, 2) {
            $pixels = $size * $scale
            $suffix = if ($scale -eq 2) { '@2x' } else { '' }
            & /usr/bin/sips -z $pixels $pixels (Join-Path $repo 'branding/KittyClaw-Picto-Posture-07.png') `
                --out (Join-Path $iconset "icon_${size}x${size}${suffix}.png") | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Icon resizing failed.' }
        }
    }
    $icon = Join-Path $scratch 'KittyClaw.icns'
    & /usr/bin/iconutil -c icns $iconset -o $icon
    if ($LASTEXITCODE -ne 0) { throw 'macOS icon generation failed.' }

    # '-' is a local ad-hoc identity, not Developer ID. Gatekeeper approval is
    # still required for downloaded applications. No notarization is requested.
    dotnet vpk pack --packId "Ekioo.KittyClaw.$Runtime.Experimental" --packVersion $Version `
        --packDir $stage --mainExe KittyClaw.Web --packTitle 'KittyClaw Experimental' `
        --packAuthors Ekioo --runtime $Runtime --channel $channel --delta None `
        --icon $icon --signAppIdentity '-' `
        --signEntitlements (Join-Path $repo 'tools/macos/KittyClaw.entitlements') `
        --instReadme (Join-Path $repo 'tools/macos/INSTALL.txt') --outputDir $OutputDir
    if ($LASTEXITCODE -ne 0) { throw 'Velopack macOS packaging failed.' }
    foreach ($pattern in '*-Setup.pkg', '*-Portable.zip', '*-full.nupkg', "releases.$channel.json") {
        if (-not (Get-ChildItem -LiteralPath $OutputDir -Filter $pattern)) { throw "Missing artifact: $pattern" }
    }
    Copy-Item -LiteralPath (Join-Path $repo 'tools/macos/INSTALL.txt') -Destination $OutputDir
    Write-Host "Experimental macOS installer and feed: $OutputDir" -ForegroundColor Green
}
finally { Pop-Location }
