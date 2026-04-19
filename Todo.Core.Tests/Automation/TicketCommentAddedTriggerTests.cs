namespace Todo.Core.Tests.Automation;

public class TicketCommentAddedTriggerTests
{
    [Fact]
    public void ignores_non_comment_signal()
    {
        var t = new TicketCommentAddedTrigger(new TicketCommentAddedTriggerSpec());
        Assert.False(t.TryHandleExternalSignal(new object(), out var f));
        Assert.Empty(f);
    }

    [Fact]
    public void empty_authors_filter_matches_any_comment()
    {
        var t = new TicketCommentAddedTrigger(new TicketCommentAddedTriggerSpec());
        Assert.True(t.TryHandleExternalSignal(new CommentAddedSignal(12, "programmer", "done"), out var f));
        Assert.Equal(12, Assert.Single(f).TicketId);
    }

    [Fact]
    public void authors_filter_matches_listed_author()
    {
        var spec = new TicketCommentAddedTriggerSpec { Authors = new() { "owner" } };
        var t = new TicketCommentAddedTrigger(spec);
        Assert.True(t.TryHandleExternalSignal(new CommentAddedSignal(1, "owner", "hi"), out var f));
        Assert.NotEmpty(f);
    }

    [Fact]
    public void authors_filter_case_insensitive()
    {
        var spec = new TicketCommentAddedTriggerSpec { Authors = new() { "Owner" } };
        var t = new TicketCommentAddedTrigger(spec);
        Assert.True(t.TryHandleExternalSignal(new CommentAddedSignal(1, "owner", "hi"), out _));
    }

    [Fact]
    public void authors_filter_rejects_unlisted_author()
    {
        var spec = new TicketCommentAddedTriggerSpec { Authors = new() { "owner" } };
        var t = new TicketCommentAddedTrigger(spec);
        Assert.False(t.TryHandleExternalSignal(new CommentAddedSignal(1, "programmer", "hi"), out var f));
        Assert.Empty(f);
    }
}
