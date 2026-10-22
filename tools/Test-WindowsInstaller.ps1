# Smoke-test a built portable payload and its real update package without installing
# over a user's application, touching their data, or starting a provider CLI.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsDir,
    [int] $Port = 5232
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This smoke test requires Windows.' }
if ($Port -in 5230, 5231) { throw 'Never verify installers against a main-instance port.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ArtifactsDir = (Resolve-Path $ArtifactsDir).Path
$scratch = Join-Path $repo ("publish-windows/verification-" + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $scratch 'app'
$data = Join-Path $scratch 'data'
New-Item -ItemType Directory -Path $scratch | Out-Null
New-Item -ItemType Directory -Path $data | Out-Null
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $probe.Start() } finally { $probe.Stop() }

$zip = Get-ChildItem -LiteralPath $ArtifactsDir -Filter '*Portable.zip' | Select-Object -First 1
if (-not $zip) { throw 'Missing portable payload from vpk pack.' }
[IO.Compression.ZipFile]::ExtractToDirectory($zip.FullName, $payload)
$payload = Join-Path $payload 'current'
$runtime = Get-Content -LiteralPath (Join-Path $payload 'KittyClaw.Web.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App', 'Microsoft.WindowsDesktop.App') {
    if ($framework -notin $runtime.runtimeOptions.includedFrameworks.name) { throw "Runtime is not embedded: $framework" }
}
$startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'KittyClaw.Web.exe'))
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WorkingDirectory = $scratch
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
foreach ($argument in @('--urls', "http://localhost:$Port", '--KITTYCLAW_DATA_DIR', $data,
    '--KITTYCLAW_DESKTOP', 'false')) { $startInfo.ArgumentList.Add($argument) }
$startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
$startInfo.Environment['DOTNET_ROOT'] = Join-Path $scratch 'no-external-dotnet'
$startInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
# No project exists in this fresh registry; also make real providers unavailable.
foreach ($provider in 'CLAUDE', 'CODEX', 'GROK', 'MISTRAL') {
    $startInfo.Environment["KITTYCLAW_${provider}_BIN"] = Join-Path $scratch 'no-provider.exe'
}
$process = [Diagnostics.Process]::Start($startInfo)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw "Published app exited: $($stderr.GetAwaiter().GetResult())" }
        try {
            $projects = Invoke-RestMethod "http://localhost:$Port/api/projects" -TimeoutSec 2
            if (@($projects).Count -ne 0) { throw 'Fresh isolated registry unexpectedly contains projects.' }
            $ready = $true
            break
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Published app did not become ready.' }
    $homeResponse = Invoke-WebRequest "http://localhost:$Port/" -TimeoutSec 10
    if ($homeResponse.StatusCode -ne 200) { throw 'Published home page failed.' }
    if ($homeResponse.Content -notmatch 'KittyClaw v\d+\.\d+') {
        throw 'The footer did not render the application version.'
    }
    $css = Invoke-WebRequest "http://localhost:$Port/css/base.css" -TimeoutSec 10
    if ($css.StatusCode -ne 200) { throw 'Published static assets failed.' }
    $blazorResponse = Invoke-WebRequest "http://localhost:$Port/_framework/blazor.web.js" -TimeoutSec 10
    if ($blazorResponse.StatusCode -ne 200) { throw 'Blazor browser bootstrap is missing.' }
    Write-Host 'Self-contained host, isolated registry, home page and static assets verified.' -ForegroundColor Green
}
finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $scratch 'stdout.log')
    $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $scratch 'stderr.log')
    $process.Dispose()
}

# Exercise Velopack's actual feed parsing, full-package download and hash validation.
# TestVelopackLocator makes no installation, registry, shortcut or running-process changes.
$feedTest = Join-Path $scratch 'verify-feed.cs'
@'
#:package Velopack@1.2.161
using Velopack;
using Velopack.Locators;
var packages = Path.Combine(args[1], "downloaded-packages");
Directory.CreateDirectory(packages);
var locator = new TestVelopackLocator("Ekioo.KittyClaw", "0.0.0-0", packages);
var manager = new UpdateManager(args[0], new UpdateOptions { ExplicitChannel = "win" }, locator);
var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("No update in built feed.");
await manager.DownloadUpdatesAsync(update);
if (manager.UpdatePendingRestart?.Version != update.TargetFullRelease.Version)
    throw new Exception("Downloaded package was not prepared for restart.");
Console.WriteLine($"Verified update package {update.TargetFullRelease.Version}.");
'@ | Set-Content -LiteralPath $feedTest
dotnet run --file $feedTest -- $ArtifactsDir $scratch
if ($LASTEXITCODE -ne 0) { throw 'Real Velopack package verification failed.' }
Write-Host "Installer smoke test passed. Logs: $scratch" -ForegroundColor Green
