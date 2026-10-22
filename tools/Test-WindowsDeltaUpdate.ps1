[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsDir,
    [Parameter(Mandatory)][string] $BaselineVersion,
    [Parameter(Mandatory)][string] $TargetVersion
)
$ErrorActionPreference = 'Stop'
$ArtifactsDir = (Resolve-Path $ArtifactsDir).Path
$scratch = Join-Path $PSScriptRoot ("../publish-windows/delta-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$testSource = Join-Path $scratch 'verify-delta.cs'
@'
#:package Velopack@1.2.161
using System.IO.Compression;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
var artifacts = args[0];
var scratch = args[1];
var baselineVersion = args[2];
var targetVersion = args[3];
var feedDir = Path.Combine(scratch, "feed");
var packages = Path.Combine(scratch, "client", "packages");
var appDir = Path.Combine(scratch, "client", "current");
Directory.CreateDirectory(feedDir);
Directory.CreateDirectory(packages);
Directory.CreateDirectory(appDir);
using (var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifacts, "releases.win.json"))))
{
    var assets = feed.RootElement.GetProperty("Assets").EnumerateArray()
        .Where(asset => asset.GetProperty("Version").GetString() == targetVersion).ToArray();
    using var stream = File.Create(Path.Combine(feedDir, "releases.win.json"));
    using var writer = new Utf8JsonWriter(stream);
    writer.WriteStartObject();
    writer.WriteStartArray("Assets");
    foreach (var asset in assets) asset.WriteTo(writer);
    writer.WriteEndArray();
    writer.WriteEndObject();
}
File.Copy(Path.Combine(artifacts, $"Ekioo.KittyClaw-{baselineVersion}-full.nupkg"), Path.Combine(packages, $"Ekioo.KittyClaw-{baselineVersion}-full.nupkg"));
File.Copy(Path.Combine(artifacts, $"Ekioo.KittyClaw-{targetVersion}-delta.nupkg"), Path.Combine(feedDir, $"Ekioo.KittyClaw-{targetVersion}-delta.nupkg"));
using (var zip = ZipFile.OpenRead(Path.Combine(artifacts, "Ekioo.KittyClaw-win-Portable.zip")))
    (zip.GetEntry("Update.exe") ?? throw new Exception("Updater missing from portable ZIP."))
        .ExtractToFile(Path.Combine(scratch, "Update.exe"));
var locator = new TestVelopackLocator("Ekioo.KittyClaw", baselineVersion, packages,
    appDir, Path.Combine(scratch, "client"), Path.Combine(scratch, "Update.exe"), channel: "win");
var manager = new UpdateManager(feedDir, new UpdateOptions { ExplicitChannel = "win" }, locator);
var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("No update available.");
if (update.DeltasToTarget.Length == 0) throw new Exception("The client did not select a delta update.");
if (update.TargetFullRelease.Version.ToString() != targetVersion) throw new Exception("Wrong target version selected.");
// No full target package exists in feedDir: falling back to a full download must fail.
await manager.DownloadUpdatesAsync(update);
if (manager.UpdatePendingRestart?.Version.ToString() != targetVersion)
    throw new Exception("Delta did not reconstruct a valid full package ready for restart.");
Console.WriteLine($"Verified incremental update {baselineVersion} -> {targetVersion}, without a full download.");
'@ | Set-Content -LiteralPath $testSource
dotnet run --file $testSource -- $ArtifactsDir $scratch $BaselineVersion $TargetVersion
if ($LASTEXITCODE -ne 0) { throw 'Incremental update verification failed.' }
