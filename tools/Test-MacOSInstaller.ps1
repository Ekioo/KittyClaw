[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BaselineDir,
    [Parameter(Mandatory)][string] $TargetDir,
    [Parameter(Mandatory)][string] $BaselineVersion,
    [Parameter(Mandatory)][string] $TargetVersion,
    [Parameter(Mandatory)][ValidateSet('osx-arm64', 'osx-x64')][string] $Runtime,
    [int] $Port = 5232
)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'This smoke test requires macOS.' }
if ($Port -in 5230, 5231) { throw 'Never verify against a main-instance port.' }
$hostRuntime = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
if ($Runtime -ne $hostRuntime) { throw "Use a native $Runtime runner; this host is $hostRuntime." }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BaselineDir = (Resolve-Path $BaselineDir).Path
$TargetDir = (Resolve-Path $TargetDir).Path
$scratch = Join-Path $repo ("publish-macos/verification-" + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $scratch 'data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
# Settings must exist even if GitHub is unavailable on the initial startup.
'{"Language":"en","OnboardingSeen":true}' | Set-Content (Join-Path $data 'settings.json')
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } finally { $listener.Stop() }

$zip = @(Get-ChildItem -LiteralPath $BaselineDir -Filter '*-Portable.zip')
if ($zip.Count -ne 1) { throw 'Expected exactly one baseline portable ZIP.' }
# ditto preserves executable permissions and bundle symlinks, unlike ZIP APIs.
& /usr/bin/ditto -x -k $zip[0].FullName $scratch
if ($LASTEXITCODE -ne 0) { throw 'Could not extract baseline bundle.' }
$bundles = @(Get-ChildItem -LiteralPath $scratch -Directory -Filter '*.app')
if ($bundles.Count -ne 1) { throw 'Expected exactly one extracted .app.' }
$bundle = $bundles[0].FullName
$payload = Join-Path $bundle 'Contents/MacOS'
$exe = Join-Path $payload 'KittyClaw.Web'
$runtimeConfig = Get-Content (Join-Path $payload 'KittyClaw.Web.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App') {
    if ($framework -notin $runtimeConfig.runtimeOptions.includedFrameworks.name) { throw "Missing embedded $framework" }
}
$aspnet = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.AspNetCore.App'
if ([version]$aspnet.version -lt [version]'10.0.15') { throw 'Embedded ASP.NET Core is below the repository security minimum 10.0.15.' }
& /usr/bin/codesign --verify --deep --strict $bundle
if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc bundle signature is invalid.' }
if (-not (Test-Path (Join-Path $payload 'UpdateMac'))) { throw 'Native Velopack updater is missing.' }

# Inspect the real installer payload without running its postinstall script or
# installing into the runner's Applications directory.
$targetPkg = @(Get-ChildItem -LiteralPath $TargetDir -Filter '*-Setup.pkg')
if ($targetPkg.Count -ne 1) { throw 'Expected exactly one target installer.' }
$expanded = Join-Path $scratch 'installer-expanded'
& /usr/sbin/pkgutil --expand-full $targetPkg[0].FullName $expanded
if ($LASTEXITCODE -ne 0) { throw 'Could not expand the target .pkg installer.' }
$installerBundles = @(Get-ChildItem -LiteralPath $expanded -Directory -Recurse -Filter '*.app')
if ($installerBundles.Count -ne 1) { throw 'Installer must contain exactly one .app.' }
& /usr/bin/codesign --verify --deep --strict $installerBundles[0].FullName
if ($LASTEXITCODE -ne 0) { throw 'Installer payload signature is invalid.' }
$installerAssembly = Join-Path $installerBundles[0].FullName 'Contents/MacOS/KittyClaw.Web.dll'
$expectedAssemblyHash = (Get-FileHash -LiteralPath $installerAssembly -Algorithm SHA256).Hash

function Test-PublishedHost([string] $ExpectedVersion) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $scratch # Finder's cwd must not determine content roots.
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--urls', "http://localhost:$Port", '--KITTYCLAW_DATA_DIR', $data,
        '--KITTYCLAW_DESKTOP', 'false')) { $start.ArgumentList.Add($argument) }
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $start.Environment['DOTNET_ROOT'] = Join-Path $scratch 'no-external-dotnet'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    # Installed hosts normally poll GitHub at startup. Fail external HTTP fast so
    # the smoke test does not depend on GitHub availability or download real releases.
    foreach ($proxy in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY') {
        $start.Environment[$proxy] = 'http://127.0.0.1:1'
    }
    $start.Environment['NO_PROXY'] = 'localhost,127.0.0.1,::1'
    foreach ($provider in 'CLAUDE', 'CODEX', 'GROK', 'MISTRAL') {
        $start.Environment["KITTYCLAW_${provider}_BIN"] = Join-Path $scratch 'no-provider'
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            if ($process.HasExited) { throw "Published host exited: $($stderr.GetAwaiter().GetResult())" }
            try {
                $response = Invoke-WebRequest "http://localhost:$Port/api/projects" -TimeoutSec 2
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            }
            catch { Start-Sleep -Milliseconds 250 }
        }
        if (-not $ready) { throw 'Published host did not become ready.' }
        $projects = Invoke-RestMethod "http://localhost:$Port/api/projects" -TimeoutSec 10
        if (@($projects).Count -ne 0) { throw 'The isolated registry unexpectedly contains projects.' }
        $home = Invoke-WebRequest "http://localhost:$Port/" -TimeoutSec 10
        $displayVersion = $ExpectedVersion.Split('-')[0]
        if ($home.Content -notmatch [regex]::Escape("KittyClaw v$displayVersion")) { throw "Wrong footer version; expected $displayVersion" }
        foreach ($path in 'css/base.css', '_framework/blazor.web.js') {
            $response = Invoke-WebRequest "http://localhost:$Port/$path" -TimeoutSec 10
            if ($response.StatusCode -ne 200) { throw "Missing static asset: $path" }
        }
    }
    finally {
        # SIGTERM lets the host flush SQLite and stop hosted services normally.
        if (-not $process.HasExited) {
            & /bin/kill -TERM $process.Id
            if (-not $process.WaitForExit(20000)) { $process.Kill($true); $process.WaitForExit(); throw 'Host did not stop gracefully.' }
        }
        $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $scratch "$ExpectedVersion.stdout.log")
        $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $scratch "$ExpectedVersion.stderr.log")
        $process.Dispose()
    }
}

Test-PublishedHost $BaselineVersion
if (-not (Test-Path (Join-Path $data 'registry.db'))) { throw 'No isolated SQLite registry was created.' }
# Persist a real settings value as well as a sentinel outside the app bundle.
$settingsPath = Join-Path $data 'settings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
$settings['Language'] = 'fr'
$settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath
$sentinel = [Guid]::NewGuid().ToString('N')
Set-Content (Join-Path $data 'preserve.txt') $sentinel
& /usr/bin/sqlite3 (Join-Path $data 'registry.db') "CREATE TABLE InstallerSmokeTest (Value TEXT NOT NULL); INSERT INTO InstallerSmokeTest VALUES ('$sentinel');"
if ($LASTEXITCODE -ne 0) { throw 'Could not persist SQLite test data.' }

dotnet run --file (Join-Path $repo 'tools/macos/VerifyUpdate.cs') -- `
    $bundle $TargetDir $scratch $BaselineVersion $TargetVersion "$Runtime-experimental"
if ($LASTEXITCODE -ne 0) { throw 'Real Velopack download/apply probe failed.' }
$updaterPid = [int](Get-Content (Join-Path $scratch 'updater.pid'))
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while (Get-Process -Id $updaterPid -ErrorAction SilentlyContinue) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Native updater did not finish within 60 seconds.' }
    Start-Sleep -Milliseconds 250
}
& /usr/bin/codesign --verify --deep --strict $bundle
if ($LASTEXITCODE -ne 0) { throw 'Updated bundle signature is invalid.' }
if ((Get-FileHash -LiteralPath (Join-Path $payload 'KittyClaw.Web.dll') -Algorithm SHA256).Hash -ne $expectedAssemblyHash) {
    throw 'The update payload does not match the target installer.'
}
Test-PublishedHost $TargetVersion
if ((Get-Content (Join-Path $data 'preserve.txt')).Trim() -ne $sentinel) { throw 'Update replaced application data.' }
$saved = & /usr/bin/sqlite3 (Join-Path $data 'registry.db') 'SELECT Value FROM InstallerSmokeTest;'
if ($LASTEXITCODE -ne 0 -or $saved -ne $sentinel) { throw 'Update lost SQLite data.' }
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
if ($settings['Language'] -ne 'fr') { throw 'Update lost the saved language.' }
Write-Host "Verified native $Runtime bundle, assets, full-package validation, real replacement and data preservation. Logs: $scratch" -ForegroundColor Green
