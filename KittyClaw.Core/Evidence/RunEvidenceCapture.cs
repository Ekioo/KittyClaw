using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Evidence;

/// <summary>
/// Assembles a <see cref="TicketEvidence"/> bundle from observable runner artifacts:
/// process exit codes, infrastructure run-events, and git filesystem state.
/// Never reads model-generated assistant text as an evidence source.
/// </summary>
public static class RunEvidenceCapture
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Returns the lowercase evidence provider string for a <see cref="CliProvider"/>.</summary>
    public static string ProviderName(CliProvider provider) => provider switch
    {
        CliProvider.Claude => "claude",
        CliProvider.Codex => "codex",
        CliProvider.Grok => "grok",
        CliProvider.Mistral => "mistral",
        _ => "unknown",
    };

    /// <summary>
    /// Captures a <see cref="TicketEvidence"/> bundle from a completed <see cref="AgentRun"/>.
    /// Observable sources used (no model text):
    /// <list type="bullet">
    ///   <item>Process exit code → <see cref="CommandRecord"/> (source = "process-exit", trust = Verified).</item>
    ///   <item>Infrastructure run-events (reset / fallback) → <see cref="RetryRecord"/> entries (source = "process-exit", trust = Verified).</item>
    ///   <item>Git state at <paramref name="workspacePath"/> → <see cref="RepositoryState"/> and <see cref="ChangedFile"/> list (source = "git", trust = Verified).</item>
    /// </list>
    /// When git probing fails (directory is not a repo, or git is absent), <see cref="RepositoryState"/>
    /// is set with <see cref="EvidenceTrust.Missing"/> so consumers surface the gap explicitly.
    /// </summary>
    /// <param name="run">The completed (or stopped / failed) agent run.</param>
    /// <param name="workspacePath">Absolute path to the agent workspace (git working tree root).</param>
    /// <param name="provider">Lowercase CLI provider name: "claude", "codex", "grok", "mistral", or "unknown".</param>
    /// <param name="ct">Cancellation token forwarded to git subprocess invocations.</param>
    public static async Task<TicketEvidence> CaptureAsync(
        AgentRun run,
        string workspacePath,
        string provider,
        CancellationToken ct = default)
    {
        var evidence = new TicketEvidence
        {
            TicketId = run.TicketId?.ToString() ?? "unknown",
            ProjectSlug = run.ProjectSlug,
            CapturedAt = DateTime.UtcNow,
        };
        evidence.RunIds.Add(run.RunId);

        // Process exit is always an observable, verified fact.
        evidence.CommandsRun.Add(new CommandRecord(
            Command: provider,
            WorkingDirectory: workspacePath,
            ExitCode: run.ExitCode,
            Success: run.ExitCode == 0,
            OutputSummary: $"status={run.Status}",
            TestsPassed: null,
            TestsFailed: null,
            Provenance: MakeProvenance(provider, "process-exit", run.RunId)));

        // "reset" and "fallback" events are emitted by the runner infrastructure,
        // not by the agent model, so they count as verified observable artifacts.
        AppendRetries(evidence, run, provider);

        await CaptureGitAsync(evidence, workspacePath, provider, run.RunId, ct);

        evidence.Status = ProvenanceRules.ComputeStatus(evidence);
        return evidence;
    }

    private static void AppendRetries(TicketEvidence evidence, AgentRun run, string provider)
    {
        var retryEvents = run.SnapshotBuffer()
            .Where(e => e.Kind is "reset" or "fallback")
            .ToList();
        for (var i = 0; i < retryEvents.Count; i++)
        {
            evidence.Retries.Add(new RetryRecord(
                AttemptNumber: i + 1,
                FailureReason: retryEvents[i].Text,
                Provenance: MakeProvenance(provider, "process-exit", run.RunId)));
        }
    }

    private static async Task CaptureGitAsync(
        TicketEvidence evidence,
        string workspacePath,
        string provider,
        string runId,
        CancellationToken ct)
    {
        string? branch = null;
        string? sha = null;
        bool? isClean = null;
        var untracked = new List<string>();
        var gitAccessible = false;

        try
        {
            var branchResult = await ProcessRunner.RunAsync("git", "branch --show-current",
                workingDirectory: workspacePath, timeout: GitTimeout, ct: ct);
            if (branchResult.Success)
            {
                gitAccessible = true;
                branch = OrNull(branchResult.Stdout.Trim());
            }

            var shaResult = await ProcessRunner.RunAsync("git", "rev-parse HEAD",
                workingDirectory: workspacePath, timeout: GitTimeout, ct: ct);
            if (shaResult.Success)
            {
                gitAccessible = true;
                sha = OrNull(shaResult.Stdout.Trim());
            }

            var statusResult = await ProcessRunner.RunAsync("git", "status --porcelain",
                workingDirectory: workspacePath, timeout: GitTimeout, ct: ct);
            if (statusResult.Success)
            {
                gitAccessible = true;
                var statusLines = statusResult.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                isClean = statusLines.Length == 0;
                foreach (var line in statusLines)
                    if (line.Length >= 2 && line[0] == '?' && line[1] == '?')
                        untracked.Add(line.Length > 3 ? line[3..].Trim() : "");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* git not installed or spawn error — fall through to Missing */ }

        if (!gitAccessible)
        {
            evidence.RepositoryState = new RepositoryState(
                null, null, null, [],
                new EvidenceProvenance("git", provider, runId, DateTime.UtcNow, EvidenceTrust.Missing));
            return;
        }

        evidence.RepositoryState = new RepositoryState(
            branch, sha, isClean, untracked,
            MakeProvenance(provider, "git", runId));

        try
        {
            var diffResult = await ProcessRunner.RunAsync("git", "diff --name-status HEAD",
                workingDirectory: workspacePath, timeout: GitTimeout, ct: ct);
            if (diffResult.Success)
                ParseDiff(evidence, diffResult.Stdout, MakeProvenance(provider, "git", runId));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* diff unavailable — no ChangedFile entries captured */ }
    }

    private static void ParseDiff(TicketEvidence evidence, string diffOutput, EvidenceProvenance prov)
    {
        foreach (var raw in diffOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var statusCode = parts[0].Trim();
            var path = parts[^1].Trim();
            var kind = statusCode.Length > 0 ? statusCode[0] switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'R' => FileChangeKind.Renamed,
                _ => FileChangeKind.Modified,
            } : FileChangeKind.Modified;
            evidence.ChangedFiles.Add(new ChangedFile(path, kind, null, prov));
        }
    }

    private static EvidenceProvenance MakeProvenance(string provider, string source, string runId) =>
        new(source, provider, runId, DateTime.UtcNow, EvidenceTrust.Verified);

    private static string? OrNull(string s) => string.IsNullOrEmpty(s) ? null : s;
}
