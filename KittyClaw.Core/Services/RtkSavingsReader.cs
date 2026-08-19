using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Services;

public sealed record RtkDailySavings(DateOnly Day, long InputTokens, long SavedTokens, int Commands);

public interface IRtkSavingsReader
{
    Task<IReadOnlyList<RtkDailySavings>> ReadDailyAsync(string workspacePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads RTK's local, project-scoped gain ledger. The command is only called while the
/// durable cost snapshot is refreshed, so opening the costs page never starts a process.
/// </summary>
public sealed class RtkSavingsReader : IRtkSavingsReader
{
    public async Task<IReadOnlyList<RtkDailySavings>> ReadDailyAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var binary = Environment.GetEnvironmentVariable("KITTYCLAW_RTK_BIN");
        if (string.IsNullOrWhiteSpace(binary)) binary = OperatingSystem.IsWindows() ? "rtk.exe" : "rtk";

        try
        {
            var startInfo = new ProcessStartInfo(binary)
            {
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("gain");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add("--daily");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("json");
            startInfo.Environment["RTK_TELEMETRY_DISABLED"] = "1";

            using var process = Process.Start(startInfo);
            if (process is null) return [];

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            var output = await stdout;
            _ = await stderr;
            if (process.ExitCode != 0) return [];
            return ParseDaily(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<RtkDailySavings> ParseDaily(string json)
    {
        var payload = JsonSerializer.Deserialize<RtkGainPayload>(json);
        if (payload?.Daily is null) return [];

        return payload.Daily
            .Where(row => row.Commands > 0 && row.SavedTokens > 0 && DateOnly.TryParse(row.Date, out _))
            .Select(row => new RtkDailySavings(
                DateOnly.Parse(row.Date!),
                Math.Max(0, row.InputTokens),
                row.SavedTokens,
                row.Commands))
            .OrderBy(row => row.Day)
            .ToArray();
    }

    private sealed record RtkGainPayload([property: JsonPropertyName("daily")] IReadOnlyList<RtkGainDay>? Daily);

    private sealed record RtkGainDay(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("commands")] int Commands,
        [property: JsonPropertyName("input_tokens")] long InputTokens,
        [property: JsonPropertyName("saved_tokens")] long SavedTokens);
}
