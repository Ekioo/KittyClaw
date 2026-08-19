using KittyClaw.Core.Services;
using KittyClaw.Core.Models;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task Images_ArePersistedInOrderAndSurviveServiceRestart()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-images");
        var chats = new ChatService(projects);
        var images = new[]
        {
            new ChatMessageImage("data:image/png;base64,first", "image/png", "first.png", 12),
            new ChatMessageImage("data:image/webp;base64,second", "image/webp", "second.webp", 34),
        };

        await chats.AppendAsync(project.Slug, "owner-chat", "user", "Compare", images: images);

        var restarted = new ChatService(new ProjectService(tmp.Path));
        var row = Assert.Single(await restarted.ListAsync(project.Slug, "owner-chat"));
        var restored = ChatService.DeserializeImages(row.ImagesJson);
        Assert.Equal(images, restored);
    }

    [Fact]
    public async Task MessagesWithoutImages_StayCompatibleAndMalformedLegacyJsonIsIgnored()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-no-images");
        var chats = new ChatService(projects);

        await chats.AppendAsync(project.Slug, "owner-chat", "user", "Text only");

        var row = Assert.Single(await chats.ListAsync(project.Slug, "owner-chat"));
        Assert.Null(row.ImagesJson);
        Assert.Empty(ChatService.DeserializeImages(row.ImagesJson));
        Assert.Empty(ChatService.DeserializeImages("not-json"));
    }

    [Fact]
    public async Task AnyAsync_UsesConversationExistenceWithoutLoadingHistory()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-any");
        var chats = new ChatService(projects);

        Assert.False(await chats.AnyAsync(project.Slug, "owner-chat"));

        await chats.AppendAsync(project.Slug, "owner-chat", "user", "Hello");

        Assert.True(await chats.AnyAsync(project.Slug, "owner-chat"));
        Assert.False(await chats.AnyAsync(project.Slug, "another-target"));
    }

    [Fact]
    public async Task MemoryCandidates_AdvanceBySegmentAndSurviveServiceRestart()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-memory-checkpoint");
        var chats = new ChatService(projects);
        await chats.AppendAsync(project.Slug, "programmer", "user", "First lesson");
        await chats.AppendAsync(project.Slug, "programmer", "assistant", "First answer");

        var first = Assert.Single(await chats.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow));
        Assert.Equal(0, first.LastConsolidatedMessageId);
        await chats.RecordMemoryResultAsync(project.Slug, "programmer", first.LatestMessageId,
            "NoChanges", 0, null, null);

        var restarted = new ChatService(new ProjectService(tmp.Path));
        Assert.Empty(await restarted.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow));

        await restarted.AppendAsync(project.Slug, "programmer", "user", "Second lesson");
        var second = Assert.Single(await restarted.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow));
        Assert.Equal(first.LatestMessageId, second.LastConsolidatedMessageId);
        Assert.Single(await restarted.ListSegmentAsync(project.Slug, "programmer",
            second.LastConsolidatedMessageId, second.LatestMessageId));
    }

    [Fact]
    public async Task MemoryFailure_PreservesCheckpointAndHonorsRetryTime()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-memory-retry");
        var chats = new ChatService(projects);
        await chats.AppendAsync(project.Slug, "programmer", "user", "Retry me");
        var now = DateTime.UtcNow;
        var candidate = Assert.Single(await chats.ListMemoryCandidatesAsync(
            project.Slug, now.AddMinutes(1), now));

        await chats.RecordMemoryFailureAsync(project.Slug, "programmer", 0, 1,
            "temporary failure", now.AddMinutes(2));

        Assert.Empty(await chats.ListMemoryCandidatesAsync(project.Slug, now.AddMinutes(1), now.AddMinutes(1)));
        var retry = Assert.Single(await chats.ListMemoryCandidatesAsync(
            project.Slug, now.AddMinutes(4), now.AddMinutes(3)));
        Assert.Equal(0, retry.LastConsolidatedMessageId);
        Assert.Equal(1, retry.AttemptCount);
        Assert.Equal(candidate.LatestMessageId, retry.LatestMessageId);
    }

    [Fact]
    public async Task ClearConversation_AlsoClearsMemoryCheckpoint()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-memory-clear");
        var chats = new ChatService(projects);
        await chats.AppendAsync(project.Slug, "programmer", "user", "Old conversation");
        var old = Assert.Single(await chats.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow));
        await chats.RecordMemoryResultAsync(project.Slug, "programmer", old.LatestMessageId,
            "NoChanges", 0, null, null);

        await chats.ClearAsync(project.Slug, "programmer");
        await chats.AppendAsync(project.Slug, "programmer", "user", "New conversation");

        var fresh = Assert.Single(await chats.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow));
        Assert.Equal(0, fresh.LastConsolidatedMessageId);
    }

    [Fact]
    public async Task RecentMessage_DefersTheWholeConversationSegment()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-memory-inactivity");
        var chats = new ChatService(projects);
        await chats.AppendAsync(project.Slug, "programmer", "user", "Still active");

        Assert.Empty(await chats.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow));
    }
}
