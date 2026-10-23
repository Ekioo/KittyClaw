using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KittyClaw.Core.Evidence;
using Markdig;

// I/O workloads use a disposable TestServer and synthetic SQLite databases only.
return BenchmarkProgram.Run(args);

internal static class BenchmarkProgram
{
    internal const string Suite = "core-io-v3";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static object? sink;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "compare")
                return Compare(Read(args[1]), Read(args[2]), double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            if (args.Length != 2 || args[0] != "run")
                throw new ArgumentException("Usage: run <report.json> | compare <baseline.json> <current.json> <threshold-percent>");
#if DEBUG
            throw new InvalidOperationException("Run benchmarks with -c Release.");
#else
            var results = new List<Measurement>();
            foreach (var size in new[] { 100, 1000 })
            {
                var evidence = Fixture(size);
                var markdown = string.Concat(Enumerable.Repeat("## Ticket\n\n- [x] Verified change\n- Pending review\n\n```cs\nvar value = 42;\n```\n\n", size));
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                results.Add(Measure($"evidence.status/{size}", () => sink = ProvenanceRules.ComputeStatus(evidence)));
                results.Add(Measure($"evidence.brief/{size}", () => sink = DecisionBriefComposer.Compose(evidence)));
                results.Add(Measure($"evidence.json/{size}", () => sink = JsonSerializer.Serialize(evidence)));
                results.Add(Measure($"markdown.html/{size}", () => sink = Markdown.ToHtml(markdown, pipeline)));
            }
            using (var isolated = new IsolatedBenchmarks())
            {
                isolated.InitializeAsync().GetAwaiter().GetResult();
                foreach (var workload in isolated.Workloads)
                    results.Add(MeasureAsync(workload.Name, workload.Operation).GetAwaiter().GetResult());
            }
            var report = new Report(Suite, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, DateTime.UtcNow, results);
            var path = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
            return 0;
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    internal static async Task<Measurement> MeasureAsync(string name, Func<Task> operation)
    {
        var warmup = Stopwatch.StartNew();
        do { await operation(); } while (warmup.ElapsedMilliseconds < 500);
        var iterations = 1;
        while (true)
        {
            var calibration = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) await operation();
            if (calibration.ElapsedMilliseconds >= 100) break;
            iterations = checked(iterations * 2);
        }
        var times = new double[9];
        var allocations = new double[9];
        for (var sample = 0; sample < times.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // Await continuations can move threads; count allocations across this isolated process.
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) await operation();
            times[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            allocations[sample] = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / (double)iterations;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        var result = new Measurement(name, times[4], allocations[4], iterations, times, "process");
        Console.WriteLine($"{name,-30} {result.MedianNanoseconds,12:N0} ns/op {result.BytesPerOperation,12:N0} B/op (process)");
        return result;
    }

    private static Measurement Measure(string name, Action operation)
    {
        // Warm the JIT and caches before calibrating batches to at least 100 ms.
        var warmup = Stopwatch.StartNew();
        do { operation(); } while (warmup.ElapsedMilliseconds < 500);
        var iterations = 1;
        while (true)
        {
            var calibration = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) operation();
            if (calibration.ElapsedMilliseconds >= 100) break;
            iterations = checked(iterations * 2);
        }
        var times = new double[9];
        var allocations = new double[9];
        for (var sample = 0; sample < times.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) operation();
            times[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        var result = new Measurement(name, times[4], allocations[4], iterations, times);
        Console.WriteLine($"{name,-25} {result.MedianNanoseconds,12:N0} ns/op {result.BytesPerOperation,12:N0} B/op");
        GC.KeepAlive(sink);
        return result;
    }

    private static TicketEvidence Fixture(int size)
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var provenance = new EvidenceProvenance("git", "unknown", "synthetic-run", timestamp, EvidenceTrust.Verified);
        var evidence = new TicketEvidence { TicketId = "1", ProjectSlug = "synthetic", CapturedAt = timestamp,
            Status = EvidenceStatus.Complete, RepositoryState = new("main", "abc123", true, [], provenance) };
        evidence.RunIds.Add("synthetic-run");
        for (var i = 0; i < size; i++)
        {
            evidence.ChangedFiles.Add(new($"src/File{i}.cs", FileChangeKind.Modified, "+ synthetic change", provenance));
            evidence.CommandsRun.Add(new("dotnet test", null, 0, true, "Passed", 10, 0, provenance));
        }
        return evidence;
    }

    private static Report Read(string path) => JsonSerializer.Deserialize<Report>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Empty report: {path}");

    private static int Compare(Report baseline, Report current, double threshold)
    {
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentException("Threshold must be finite and non-negative.");
        if (baseline.Suite != Suite || current.Suite != Suite || baseline.Framework != current.Framework
            || baseline.OS != current.OS || baseline.Architecture != current.Architecture || baseline.Processors != current.Processors)
            throw new InvalidDataException("Incompatible suite or environment; regenerate the baseline on the same runner.");
        Validate(baseline);
        Validate(current);
        var before = baseline.Measurements.ToDictionary(m => m.Name);
        if (!before.Keys.ToHashSet().SetEquals(current.Measurements.Select(m => m.Name)))
            throw new InvalidDataException("Scenario sets differ; regenerate the baseline.");
        var failed = false;
        foreach (var measurement in current.Measurements)
        {
            var old = before[measurement.Name];
            if (old.AllocationScope != measurement.AllocationScope)
                throw new InvalidDataException($"Allocation scope differs for {measurement.Name}.");
            Check(measurement.Name, "ns/op", old.MedianNanoseconds, measurement.MedianNanoseconds);
            Check(measurement.Name, "B/op", old.BytesPerOperation, measurement.BytesPerOperation);
        }
        return failed ? 1 : 0;

        void Check(string name, string metric, double old, double value)
        {
            var regression = value > old * (1 + threshold / 100);
            var delta = old == 0 ? (value == 0 ? "0%" : "new allocation") : $"{(value / old - 1) * 100:+0.0;-0.0;0}%";
            Console.WriteLine($"{(regression ? "FAIL" : "PASS")} {name} {metric}: {old:N0} -> {value:N0} ({delta})");
            failed |= regression;
        }
    }

    private static void Validate(Report report)
    {
        if (report.Measurements.Count == 0 || report.Measurements.Select(m => m.Name).Distinct().Count() != report.Measurements.Count
            || report.Measurements.Any(m => !double.IsFinite(m.MedianNanoseconds) || m.MedianNanoseconds <= 0
                || !double.IsFinite(m.BytesPerOperation) || m.BytesPerOperation < 0 || m.Iterations <= 0
                || m.AllocationScope is not ("current-thread" or "process")
                || m.Samples.Length != 9 || m.Samples.Any(s => !double.IsFinite(s) || s <= 0)))
            throw new InvalidDataException("Invalid or incomplete benchmark measurements.");
    }
}

internal sealed record Measurement(string Name, double MedianNanoseconds, double BytesPerOperation, int Iterations, double[] Samples, string AllocationScope = "current-thread");
internal sealed record Report(string Suite, string Framework, string OS, string Architecture, int Processors, DateTime CreatedAtUtc, List<Measurement> Measurements);
