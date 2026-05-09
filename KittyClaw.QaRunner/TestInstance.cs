using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KittyClaw.QaRunner;

/// <summary>
/// Spawns an isolated KittyClaw.Web process on a free port with a throwaway data dir,
/// waits for it to be ready, and tears it down on dispose. Pairs with the dynamic claude
/// discovery in ClaudeRunner: as long as KittyClaw.ClaudeMock has been built, it will be
/// found as a sibling of the web exe and used by the test instance automatically.
/// </summary>
public sealed class TestInstance : IAsyncDisposable
{
    public int Port { get; }
    public string ApiUrl => $"http://localhost:{Port}";
    public string DataDir { get; }

    private readonly Process _proc;
    private readonly bool _ownsDataDir;

    private TestInstance(Process proc, int port, string dataDir, bool ownsDataDir)
    {
        _proc = proc;
        Port = port;
        DataDir = dataDir;
        _ownsDataDir = ownsDataDir;
    }

    public static async Task<TestInstance> StartAsync(string webExePath, CancellationToken ct = default)
    {
        if (!File.Exists(webExePath))
            throw new FileNotFoundException($"KittyClaw.Web exe not found at {webExePath}", webExePath);

        var port = PickFreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // Pre-seed AppSettings to skip the onboarding splash on first launch — fresh data dir
        // means OnboardingSeen=false by default, which blocks the home page behind a modal.
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            """{"OnboardingSeen":true,"Language":"en"}""", ct);

        var psi = new ProcessStartInfo
        {
            FileName = webExePath,
            WorkingDirectory = Path.GetDirectoryName(webExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        psi.Environment["KITTYCLAW_DATA_DIR"] = dataDir;
        // Development: skips HSTS (no HTTPS configured here, HSTS would break HTTP requests)
        // and gives the dev-time static-asset path resolution.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        // Point content root at the source dir so MapStaticAssets / static web assets manifest
        // can resolve wwwroot paths (the bin/Debug build doesn't copy wwwroot, only references it
        // through KittyClaw.Web.staticwebassets.runtime.json).
        var contentRoot = FindContentRoot(webExePath);
        if (contentRoot is not null)
            psi.Environment["ASPNETCORE_CONTENTROOT"] = contentRoot;
        // Pin the test instance to the mock claude. In stable layouts the mock lives in qa-mock/
        // (not as a sibling of the Web exe — that would hijack real agent dispatch). In dev, walk
        // up to KittyClaw.ClaudeMock/bin/**/claude.exe. If neither resolves, leave the env var
        // unset and let the test instance fall back to whatever ResolveClaudeBinary() picks up,
        // surfacing the warning via stderr.
        var mockClaude = FindMockClaude(webExePath);
        if (mockClaude is not null)
            psi.Environment["KITTYCLAW_CLAUDE_BIN"] = mockClaude;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start KittyClaw.Web for QA");

        // Drain output so the pipe doesn't fill up. We don't surface it unless startup fails.
        var stdoutBuf = new System.Text.StringBuilder();
        var stderrBuf = new System.Text.StringBuilder();
        _ = Task.Run(async () => { try { while (await proc.StandardOutput.ReadLineAsync() is { } l) lock (stdoutBuf) stdoutBuf.AppendLine(l); } catch { } });
        _ = Task.Run(async () => { try { while (await proc.StandardError.ReadLineAsync() is { } l) lock (stderrBuf) stderrBuf.AppendLine(l); } catch { } });

        try
        {
            await WaitUntilReadyAsync(port, TimeSpan.FromSeconds(45), ct);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            string snippet;
            lock (stdoutBuf) lock (stderrBuf) snippet = $"--- stdout ---\n{stdoutBuf}\n--- stderr ---\n{stderrBuf}";
            try { Directory.Delete(dataDir, recursive: true); } catch { }
            throw new InvalidOperationException($"KittyClaw.Web on port {port} did not become ready.\n{snippet}");
        }

        return new TestInstance(proc, port, dataDir, ownsDataDir: true);
    }

    private static string? FindContentRoot(string webExePath)
    {
        // Walk up from the exe directory to find the nearest ancestor with a wwwroot folder.
        // bin/Debug/net10.0/ is the exe location; KittyClaw.Web/ (with wwwroot/) is 3 levels up.
        var dir = new DirectoryInfo(Path.GetDirectoryName(webExePath)!);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "wwwroot")))
                return dir.FullName;
        }
        return null;
    }

    private static string? FindMockClaude(string webExePath)
    {
        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var webDir = Path.GetDirectoryName(webExePath)!;

        // Stable / publish layout: mock lives in `<webDir>/qa-mock/claude.exe`.
        var qaMock = Path.Combine(webDir, "qa-mock", name);
        if (File.Exists(qaMock)) return qaMock;

        // Dev: walk up to find KittyClaw.ClaudeMock/bin/**/claude.exe.
        var dir = new DirectoryInfo(webDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var mockBin = Path.Combine(dir.FullName, "KittyClaw.ClaudeMock", "bin");
            if (Directory.Exists(mockBin))
            {
                var found = Directory.EnumerateFiles(mockBin, name, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilReadyAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://localhost:{port}/api/projects";
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Test instance did not become ready within {timeout.TotalSeconds:F0}s. Last error: {last?.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        try { await _proc.WaitForExitAsync(); } catch { }
        _proc.Dispose();
        if (_ownsDataDir)
        {
            try { Directory.Delete(DataDir, recursive: true); } catch { /* keep going */ }
        }
    }
}
