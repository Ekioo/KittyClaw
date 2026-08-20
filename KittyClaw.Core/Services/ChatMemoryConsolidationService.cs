using System.Text;
using KittyClaw.Core.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

public sealed class ChatMemoryConsolidationService(
    ProjectService projects,
    ChatService chats,
    MemberService members,
    AgentRunRegistry runs,
    AgentMemoryHandler memory,
    ILogger<ChatMemoryConsolidationService> logger,
    DurableWriteRouter? durableWrites = null) : BackgroundService
{
    public static readonly TimeSpan DefaultIdleDelay = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _idleDelay = ReadDuration("KITTYCLAW_CHAT_MEMORY_IDLE_MINUTES", DefaultIdleDelay);
    private readonly TimeSpan _pollDelay = ReadDuration("KITTYCLAW_CHAT_MEMORY_POLL_SECONDS", TimeSpan.FromSeconds(30), seconds: true);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        RunLoopAsync(async ct =>
        {
            try { await ProcessOnceAsync(DateTime.UtcNow, ct); }
            catch (Exception ex) { logger.LogError(ex, "Ad-hoc chat memory consolidation cycle failed"); }
        }, _pollDelay, stoppingToken,
        () => logger.LogInformation("Ad-hoc chat memory consolidation started (idle delay {Delay})", _idleDelay));

    internal static async Task RunLoopAsync(
        Func<CancellationToken, Task> processOnce,
        TimeSpan pollDelay,
        CancellationToken stoppingToken,
        Action? onStarted = null)
    {
        // BackgroundService.StartAsync invokes ExecuteAsync synchronously until its first
        // incomplete await. SQLite's async APIs can complete synchronously, so a large first
        // consolidation scan used to delay Kestrel binding for minutes during restart.
        await Task.Yield();

        onStarted?.Invoke();
        while (!stoppingToken.IsCancellationRequested)
        {
            await processOnce(stoppingToken);
            await Task.Delay(pollDelay, stoppingToken);
        }
    }

    public async Task ProcessOnceAsync(DateTime now, CancellationToken ct = default)
    {
        foreach (var project in await projects.ListProjectsAsync())
        {
            var workspace = projects.ResolveWorkspacePath(project);
            var candidates = await chats.ListMemoryCandidatesAsync(project.Slug, now - _idleDelay, now);
            await ProcessSequentiallyAsync(candidates, async (candidate, candidateToken) =>
            {
                if (runs.HasActiveInGroup(project.Slug, $"chat:{project.Slug}:{candidate.TargetSlug}")) return;
                var agent = ParseBaseAgent(candidate.TargetSlug);
                var memoryDir = Path.Combine(workspace, ".agents", agent, "memory");
                var legacyMemory = Path.Combine(workspace, ".agents", agent, "memory.md");
                if (agent == "owner-chat" || (!Directory.Exists(memoryDir) && !File.Exists(legacyMemory)))
                {
                    await chats.RecordMemoryResultAsync(project.Slug, candidate.TargetSlug,
                        candidate.LatestMessageId, "IgnoredNoMemory", 0, null, null);
                    logger.LogInformation("Chat memory ignored for {Project}/{Target}: no persistent memory", project.Slug, candidate.TargetSlug);
                    return;
                }
                if (await members.GetMemberBySlugAsync(project.Slug, agent) is null)
                {
                    await chats.RecordMemoryResultAsync(project.Slug, candidate.TargetSlug,
                        candidate.LatestMessageId, "IgnoredNoMember", 0, null, null);
                    return;
                }

                var segment = await chats.ListSegmentAsync(project.Slug, candidate.TargetSlug,
                    candidate.LastConsolidatedMessageId, candidate.LatestMessageId);
                try
                {
                    DurableWriteRoute? route = null;
                    var writeWorkspace = workspace;
                    if (project.WorktreesEnabled && durableWrites is not null)
                    {
                        route = await durableWrites.ResolveAsync(project.Slug, null,
                            [Path.Combine(".agents", agent, "memory")], candidateToken);
                        writeWorkspace = route.RootPath;
                    }
                    var result = await memory.ConsolidateAdHocConversationAsync(project.Slug, writeWorkspace,
                        agent, FormatTranscript(segment), candidateToken);
                    if (route is not null && durableWrites is not null)
                    {
                        var validation = await durableWrites.CommitAndQueueAsync(project.Slug, route,
                            $"chore(memory): consolidate {agent} chat memory", candidateToken);
                        if (validation.Status != DurableWriteValidationStatus.Ready)
                            throw new InvalidOperationException(validation.Error ??
                                "Consolidated memory requires review before integration.");
                    }
                    await chats.RecordMemoryResultAsync(project.Slug, candidate.TargetSlug,
                        candidate.LatestMessageId, result.ToString(), 0, null, null);
                    logger.LogInformation("Chat memory {Result} for {Project}/{Target} through message {MessageId}",
                        result, project.Slug, candidate.TargetSlug, candidate.LatestMessageId);
                }
                catch (Exception ex)
                {
                    var attempts = candidate.AttemptCount + 1;
                    var retry = now + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempts, 6))));
                    await chats.RecordMemoryFailureAsync(project.Slug, candidate.TargetSlug,
                        candidate.LastConsolidatedMessageId, attempts, ex.Message, retry);
                    logger.LogWarning(ex, "Chat memory consolidation failed for {Project}/{Target}; retry at {Retry}",
                        project.Slug, candidate.TargetSlug, retry);
                }
            }, ct);
        }
    }

    internal static async Task ProcessSequentiallyAsync<T>(
        IEnumerable<T> items,
        Func<T, CancellationToken, Task> process,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            await process(item, ct);
            // Keep the batch cooperative: one consolidation at a time, while returning
            // execution to the host between items so HTTP work is not starved.
            await Task.Yield();
        }
    }

    private static string ParseBaseAgent(string target) => target.Split('#', 2)[0];

    private static string FormatTranscript(IEnumerable<KittyClaw.Core.Models.ChatMessageRow> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
            sb.AppendLine($"[{message.CreatedAt}] {message.Role}: {message.Text}");
        return sb.ToString();
    }

    private static TimeSpan ReadDuration(string name, TimeSpan fallback, bool seconds = false)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0
            ? (seconds ? TimeSpan.FromSeconds(value) : TimeSpan.FromMinutes(value))
            : fallback;
    }
}
