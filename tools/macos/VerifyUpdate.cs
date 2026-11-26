// Run only against a disposable .app copied by Test-MacOSInstaller.ps1.
#:package Velopack@1.2.161
using System.Diagnostics;
using Velopack;
using Velopack.Locators;

if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
var bundle = Path.GetFullPath(args[0]);
var feed = Path.GetFullPath(args[1]);
var scratch = Path.GetFullPath(args[2]);
var baseline = args[3];
var target = args[4];
var channel = args[5];
if (!bundle.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.Ordinal)
    || !bundle.EndsWith(".app", StringComparison.Ordinal))
    throw new Exception("Refusing to update a bundle outside the disposable test directory.");
var process = new ProbeProcess(Path.Combine(bundle, "Contents", "MacOS", "KittyClaw.Web"),
    Path.Combine(scratch, "updater.pid"));
var locator = new ProbeLocator(process, Path.Combine(scratch, "packages"));
if (locator.Channel != channel || locator.CurrentlyInstalledVersion?.ToString() != baseline)
    throw new Exception("Real bundle manifest did not match the expected architecture/channel/version.");
var manager = new UpdateManager(feed, locator: locator);
if (!manager.IsInstalled) throw new Exception("Velopack did not recognize the .app bundle.");
var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("No update available.");
if (update.TargetFullRelease.Version.ToString() != target)
    throw new Exception("Wrong update version selected.");
await manager.DownloadUpdatesAsync(update);
if (manager.UpdatePendingRestart?.Version.ToString() != target)
    throw new Exception("Downloaded package did not validate as pending.");
// Exercise the real native updater without launching a browser or touching /Applications.
// It waits for THIS probe to exit. The parent script waits for the updater, starts the
// replaced KittyClaw server, and verifies its version and the preserved SQLite registry.
manager.WaitExitThenApplyUpdates(manager.UpdatePendingRestart, silent: true, restart: false);
Console.WriteLine($"Prepared real bundle replacement {baseline} -> {target} ({channel}).");

sealed class ProbeLocator(IProcessImpl process, string packages) : OsxVelopackLocator(process, null)
{
    public override string PackagesDir => packages;
}

sealed class ProbeProcess(string executable, string pidFile) : IProcessImpl
{
    public string GetCurrentProcessPath() => executable;
    public uint GetCurrentProcessId() => (uint)Environment.ProcessId;
    public void Exit(int exitCode) => throw new Exception("The probe must exit normally.");
    public void StartProcess(string exePath, IEnumerable<string> arguments, string? workDir, bool showWindow)
    {
        var start = new ProcessStartInfo(exePath) { UseShellExecute = false };
        if (workDir is not null) start.WorkingDirectory = workDir;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new Exception("Updater did not start.");
        File.WriteAllText(pidFile, child.Id.ToString());
    }
}
