using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Handles agent-memory automation actions: commitAgentMemory and consolidateAgentMemory.
/// Owns the git semaphore used to serialize in-process git operations per repository.
/// </summary>
public enum AdHocMemoryResult { NoChanges, Modified }
internal enum CommitMemoryResult { NoChanges, Committed, Failed, Skipped }

public sealed class AgentMemoryHandler(
    TicketService tickets,
    MemberService members,
    ProjectService projects,
    AgentRunner runner,
    SessionRegistry sessions,
    ILogger logger)
{
    // Serializes in-process git operations per repository. Keyed by the git cwd so one
    // repo's slow/hung git (bounded by ProcessRunner's timeout) can't stall other projects.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gitLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal async Task<CommitMemoryResult> ExecuteCommitAgentMemoryAsync(
        ProjectRuntime rt, CommitAgentMemoryActionSpec spec, TriggerFiring? firing = null)
    {
        try
        {
            var agent = spec.Agent;
            if (agent.Contains("{assignee}"))
            {
                if (firing?.TicketId is null)
                {
                    logger.LogInformation(
                        "commitAgentMemory: {{assignee}} placeholder but no firing ticket — skipping");
                    return CommitMemoryResult.Skipped;
                }
                var t = await tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (string.IsNullOrEmpty(t?.AssignedTo))
                {
                    logger.LogInformation(
                        "commitAgentMemory: {{assignee}} placeholder but ticket #{Id} has no assignee — skipping",
                        firing.TicketId);
                    return CommitMemoryResult.Skipped;
                }
                agent = agent.Replace("{assignee}", t.AssignedTo);
            }

            var workspace = rt.Workspace!;
            // Memory lives either in the new per-topic layout (.agents/{agent}/memory/) or, until an
            // agent has consolidated, in the legacy flat file (.agents/{agent}/memory.md). Commit
            // whichever exist — both, during the migration window.
            var memoryDirAbs = Path.Combine(workspace, ".agents", agent, "memory");
            var legacyAbs = Path.Combine(workspace, ".agents", agent, "memory.md");
            var hasDir = Directory.Exists(memoryDirAbs);
            var hasLegacy = File.Exists(legacyAbs);
            if (!hasDir && !hasLegacy)
            {
                logger.LogInformation(
                    "commitAgentMemory: no memory found for {Agent} under {Path}",
                    agent, Path.GetDirectoryName(legacyAbs));
                return CommitMemoryResult.Skipped;
            }

            // Prefer a nested .agents/.git repo if present (decouples agent config from main project repo).
            // Otherwise fall back to the main workspace repo.
            var agentsDir = Path.Combine(workspace, ".agents");
            string gitCwd;
            string relBase;
            if (Directory.Exists(Path.Combine(agentsDir, ".git")))
            {
                gitCwd = agentsDir;
                relBase = $"{agent}";
            }
            else if (Directory.Exists(Path.Combine(workspace, ".git")))
            {
                gitCwd = workspace;
                relBase = $".agents/{agent}";
            }
            else
            {
                logger.LogDebug(
                    "commitAgentMemory: no git repo at {Path} or {Agents} — skipping",
                    workspace, agentsDir);
                return CommitMemoryResult.Skipped;
            }

            // Only pass paths that exist. Some Git versions reject the entire `git add` when one
            // pathspec is absent, which prevented a new-format-only memory tree from being committed.
            var pathArgs = string.Join(" ", new[]
            {
                hasDir ? $"\"{relBase}/memory\"" : null,
                hasLegacy ? $"\"{relBase}/memory.md\"" : null,
            }.Where(path => path is not null));

            var gitLock = _gitLocks.GetOrAdd(gitCwd, _ => new SemaphoreSlim(1, 1));
            await gitLock.WaitAsync();
            try
            {
                var diff = await RunGitAsync(gitCwd, $"diff --quiet --exit-code -- {pathArgs}");
                // diff --quiet returns 1 when there are tracked-file changes; untracked new topic
                // files are invisible to it, so also check `status --porcelain` before bailing.
                var status = await RunGitAsync(gitCwd, $"status --porcelain -- {pathArgs}");
                if (diff.exitCode == 0 && string.IsNullOrWhiteSpace(status.stdout))
                {
                    logger.LogDebug("commitAgentMemory: {Agent} memory is clean, nothing to commit", agent);
                    return CommitMemoryResult.NoChanges;
                }

                var add = await RunGitAsync(gitCwd, $"add -- {pathArgs}");
                if (add.exitCode != 0)
                {
                    logger.LogWarning(
                        "commitAgentMemory: git add failed for {Agent}: {Err}", agent, add.stderr);
                    return CommitMemoryResult.Failed;
                }

                var ticketSuffix = firing?.TicketId is int tid ? $" (#{tid})" : "";
                var msg = $"chore(memory): {agent}{ticketSuffix}";
                // Memory commits are generated by KittyClaw, not by the workspace owner. Give
                // them the same stable technical identity used by agents for their own commits.
                // Besides making history explicit, this lets gitCommit triggers ignore an
                // agent's complete post-run chain (including its memory commit) and prevents
                // self-triggering loops such as documentalist -> commit memory -> documentalist.
                var identity = BuildAgentGitIdentity(agent);
                var commit = await RunGitAsync(
                    gitCwd,
                    $"-c user.name=\"{identity}\" -c user.email=\"{identity}@kittyclaw.local\" commit --no-verify -m \"{msg}\" -- {pathArgs}");
                if (commit.exitCode != 0)
                {
                    logger.LogWarning(
                        "commitAgentMemory: git commit failed for {Agent}: {Err}", agent, commit.stderr);
                    return CommitMemoryResult.Failed;
                }

                logger.LogInformation("commitAgentMemory: committed {Agent} memory", agent);
                return CommitMemoryResult.Committed;
            }
            finally { gitLock.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "commitAgentMemory: failed to commit memory for {Agent}", spec.Agent);
            return CommitMemoryResult.Failed;
        }
    }

    internal async Task ExecuteConsolidateAgentMemoryAsync(
        ProjectRuntime rt,
        ConsolidateAgentMemoryActionSpec spec,
        TriggerFiring? firing,
        AgentRun? parentRun,
        CancellationToken ct)
    {
        try
        {
            var agent = spec.Agent;
            if (agent.Contains("{assignee}"))
            {
                if (firing?.TicketId is null)
                {
                    logger.LogInformation(
                        "consolidateAgentMemory: {{assignee}} placeholder but no firing ticket — skipping");
                    return;
                }
                var t = await tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (string.IsNullOrEmpty(t?.AssignedTo))
                {
                    logger.LogInformation(
                        "consolidateAgentMemory: {{assignee}} placeholder but ticket #{Id} has no assignee — skipping",
                        firing.TicketId);
                    return;
                }
                agent = agent.Replace("{assignee}", t.AssignedTo);
            }

            if (parentRun?.Status == AgentRunStatus.Failed && (parentRun.ExitCode ?? 0) < 0)
            {
                logger.LogInformation(
                    "consolidateAgentMemory: parent run {Id} failed (exit {Exit}) — skipping",
                    parentRun.RunId, parentRun.ExitCode);
                return;
            }

            var instructionPath = Path.Combine(
                rt.Workspace!,
                spec.InstructionFile.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(instructionPath))
            {
                logger.LogWarning(
                    "consolidateAgentMemory: instruction file not found: {Path}", instructionPath);
                return;
            }

            var instructionContent = (await File.ReadAllTextAsync(instructionPath, ct))
                .Replace("{agentSlug}", agent);
            var eventsSummary = BuildEventsSummary(parentRun);

            const string scope = "consolidate";
            sessions.Clear(rt.Workspace!, $"{scope}:{agent}", ticketId: null);

            var project = await projects.GetProjectAsync(rt.Slug);
            var member = await members.GetMemberBySlugAsync(rt.Slug, agent);
            var memberModel = string.IsNullOrWhiteSpace(member?.DefaultModel) ? null : member.DefaultModel;
            var projectFallback = string.IsNullOrWhiteSpace(project?.FallbackModel) ? null : project.FallbackModel;
            var localDefault = string.IsNullOrWhiteSpace(project?.LocalModelName) ? null : project.LocalModelName;
            var effectiveModel = ActionExecutor.FirstConfiguredModel(spec.Model, memberModel, projectFallback, localDefault);
            var routing = ModelRouting.Resolve(effectiveModel, project?.LocalModelBaseUrl);
            var target = routing.ToTarget(effectiveModel);

            // Preserve the project's quota fallback when a more specific primary target won.
            // If it is already the primary, retrying the same target would only duplicate failure.
            AgentDispatchTarget? fallbackTarget = null;
            if (projectFallback is not null &&
                !string.Equals(projectFallback, effectiveModel, StringComparison.OrdinalIgnoreCase))
            {
                var fallbackRouting = ModelRouting.Resolve(projectFallback, project?.LocalModelBaseUrl);
                if (fallbackRouting.Error is null)
                    fallbackTarget = fallbackRouting.ToTarget(projectFallback);
                else
                    logger.LogWarning(
                        "consolidateAgentMemory: fallback target '{Model}' is unusable for {Agent}: {Error}",
                        projectFallback, agent, fallbackRouting.Error);
            }

            logger.LogInformation(
                "consolidateAgentMemory: resolved {Agent} to {Provider}:{Model}{Fallback}",
                agent, target.Provider, target.Model ?? "default",
                fallbackTarget is null ? "" : $" (fallback {fallbackTarget.Provider}:{fallbackTarget.Model ?? "default"})");

            var runCtx = new AgentRunContext
            {
                ProjectSlug = rt.Slug,
                WorkspacePath = rt.Workspace!,
                AgentName = agent,
                SkillFile = $"{agent}/SKILL.md",
                MaxTurns = spec.MaxTurns,
                ConcurrencyGroup = $"consolidate-{agent}",
                InlineSkillContent = instructionContent,
                ExtraContext = string.IsNullOrWhiteSpace(eventsSummary)
                    ? "No events were recorded for this run."
                    : eventsSummary,
                SessionScope = scope,
                Target = target,
                FallbackTarget = fallbackTarget,
                RetryOnResumeFailure = true,
                MaxRunDuration = TimeSpan.FromMinutes(30),
            };

            var run = await runner.RunAsync(runCtx, ct);

            var memoryPaths = $"\".agents/{agent}/memory\" \".agents/{agent}/memory.md\"";
            var diff = await RunGitAsync(rt.Workspace!, $"diff --shortstat HEAD -- {memoryPaths}");
            var diffSummary = diff.stdout.Trim();
            logger.LogInformation("consolidate {Agent}: run {Status} (exit {Exit}){Diff}",
                agent, run.Status, run.ExitCode,
                string.IsNullOrWhiteSpace(diffSummary) ? "" : $" — {diffSummary}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "consolidateAgentMemory: failed for {Agent}", spec.Agent);
        }
    }

    public async Task<AdHocMemoryResult> ConsolidateAdHocConversationAsync(
        string projectSlug, string workspace, string agent, string transcript, CancellationToken ct)
    {
        var instructionPath = Path.Combine(workspace, ".agents", "memory-consolidation.md");
        if (!File.Exists(instructionPath))
            throw new InvalidOperationException($"Memory consolidation instructions not found: {instructionPath}");

        var memoryDir = Path.Combine(workspace, ".agents", agent, "memory");
        var legacyMemory = Path.Combine(workspace, ".agents", agent, "memory.md");
        if (!Directory.Exists(memoryDir) && !File.Exists(legacyMemory))
            throw new DirectoryNotFoundException($"No persistent memory exists for agent '{agent}'.");

        var before = SnapshotMemory(memoryDir, legacyMemory);
        var instructionContent = (await File.ReadAllTextAsync(instructionPath, ct)).Replace("{agentSlug}", agent);
        var project = await projects.GetProjectAsync(projectSlug);
        var member = await members.GetMemberBySlugAsync(projectSlug, agent);
        var effectiveModel = ActionExecutor.FirstConfiguredModel(member?.DefaultModel,
            project?.FallbackModel, project?.LocalModelName);
        var routing = ModelRouting.Resolve(effectiveModel, project?.LocalModelBaseUrl);
        if (routing.Error is not null) throw new InvalidOperationException(routing.Error);

        const string scope = "consolidate-chat";
        sessions.Clear(workspace, $"{scope}:{agent}", ticketId: null);
        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = projectSlug,
            WorkspacePath = workspace,
            AgentName = agent,
            SkillFile = $"{agent}/SKILL.md",
            MaxTurns = 5,
            ConcurrencyGroup = $"consolidate-chat-{agent}",
            InlineSkillContent = instructionContent,
            ExtraContext = "## Ad-hoc conversation segment\n\n" + transcript,
            SessionScope = scope,
            Target = routing.ToTarget(effectiveModel),
            RetryOnResumeFailure = true,
            MaxRunDuration = TimeSpan.FromMinutes(30),
        }, ct);
        if (run.Status != AgentRunStatus.Completed || (run.ExitCode ?? 0) != 0)
            throw new InvalidOperationException($"Memory consolidation run {run.RunId} failed ({run.Status}, exit {run.ExitCode}).");

        var after = SnapshotMemory(memoryDir, legacyMemory);
        var rt = new ProjectRuntime(projectSlug) { Workspace = workspace };
        var commit = await ExecuteCommitAgentMemoryAsync(rt, new CommitAgentMemoryActionSpec { Agent = agent });
        if (commit == CommitMemoryResult.Failed ||
            (!before.SequenceEqual(after) && commit != CommitMemoryResult.Committed))
            throw new InvalidOperationException($"Failed to commit memory changes for agent '{agent}'.");
        return commit == CommitMemoryResult.Committed
            ? AdHocMemoryResult.Modified
            : AdHocMemoryResult.NoChanges;
    }

    private static string[] SnapshotMemory(string memoryDir, string legacyMemory)
    {
        var files = new List<string>();
        if (Directory.Exists(memoryDir)) files.AddRange(Directory.EnumerateFiles(memoryDir, "*", SearchOption.AllDirectories));
        if (File.Exists(legacyMemory)) files.Add(legacyMemory);
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{path}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
            .ToArray();
    }

    internal static string BuildEventsSummary(AgentRun? run)
    {
        if (run is null) return "";
        var lines = new List<string>();
        foreach (var ev in run.SnapshotBuffer())
        {
            if (ev.Kind is "assistant" or "tool_use" or "result")
            {
                var text = ev.Kind == "tool_use"
                    ? $"[tool_use] {ev.Text}: {TruncateDetail(ev.Detail, 120)}"
                    : $"[{ev.Kind}] {TruncateLine(ev.Text, 200)}";
                lines.Add(text);
            }
            if (lines.Count >= 80) break;
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    private static string BuildAgentGitIdentity(string agent)
    {
        var identity = new string(agent
            .ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray())
            .Trim('-', '.');
        return string.IsNullOrEmpty(identity) ? "agent" : identity;
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(string cwd, string args)
    {
        // 2-minute cap: a git blocked on a credential prompt or a stale index.lock must not
        // hold the per-repo git lock forever.
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromMinutes(2));
        return (res.ExitCode ?? -1, res.Stdout, res.TimedOut ? "git timed out after 2 minutes" : res.Stderr);
    }

    private static string TruncateLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string TruncateDetail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "{}";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
