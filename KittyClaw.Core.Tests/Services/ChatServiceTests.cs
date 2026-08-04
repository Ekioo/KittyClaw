using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ChatServiceTests
{
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
}
