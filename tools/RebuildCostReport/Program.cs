using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using Microsoft.Data.Sqlite;

if (!(args.Length == 2 && args[0] == "--apply") && !(args.Length == 3 && args[0] == "--preview"))
{
    Console.Error.WriteLine("Usage: RebuildCostReport --apply <data directory> | --preview <source data directory> <new preview directory>");
    return 1;
}
var sourceDir = Path.GetFullPath(args[1]);
var dataDir = args[0] == "--preview" ? Path.GetFullPath(args[2]) : sourceDir;
if (args[0] == "--preview")
{
    if (Directory.Exists(dataDir)) throw new ArgumentException("The preview directory must not already exist.");
    if (!File.Exists(Path.Combine(sourceDir, "registry.db"))) throw new ArgumentException("The source registry is missing.");
    Directory.CreateDirectory(dataDir);
    foreach (var source in new[] { Path.Combine(sourceDir, "registry.db") }
                 .Concat(Directory.EnumerateFiles(Path.Combine(sourceDir, "projects"), "*.db")))
    {
        var destination = Path.Combine(dataDir, Path.GetRelativePath(sourceDir, source));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        read.Open(); write.Open(); read.BackupDatabase(write);
    }
    foreach (var folder in new[] { "runs", "runtime" })
    {
        var root = Path.Combine(sourceDir, folder);
        if (!Directory.Exists(root)) continue;
        foreach (var source in Directory.EnumerateFiles(root, folder == "runs" ? "*.json" : "cost-log*.jsonl", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(dataDir, Path.GetRelativePath(sourceDir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(destination);
            input.CopyTo(output);
        }
    }
}
if (!File.Exists(Path.Combine(dataDir, "registry.db")))
    throw new ArgumentException("The directory must contain an existing KittyClaw registry.db.");
var snapshot = Path.Combine(dataDir, "cost-report-snapshot.json");
if (File.Exists(snapshot))
    File.Copy(snapshot, snapshot + ".before-accounting-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak");
var projects = new ProjectService(dataDir);
var tickets = new TicketService(projects, new MemberService(projects));
var reports = new CostReportService(projects, new PipelineService(projects), tickets,
    new RtkSavingsReader(), new CostTracker(new SessionRegistry(dataDir)));
// No host, AutomationEngine or provider dispatch is started by this utility.
await reports.RefreshAsync();
var report = await reports.GetReportAsync(new(DateOnly.MinValue, DateOnly.MaxValue));
Console.WriteLine(JsonSerializer.Serialize(new { report.TotalUsd, report.UnpricedRuns, report.Subscriptions,
    Projects = report.Projects.Select(project => new { project.ProjectSlug, project.UsdCost }),
    RateCard = ModelCostEstimator.RateCardVersion }, new JsonSerializerOptions { WriteIndented = true }));
return 0;
