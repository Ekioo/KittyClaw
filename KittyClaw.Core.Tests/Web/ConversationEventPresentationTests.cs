using KittyClaw.Web.Components;

namespace KittyClaw.Core.Tests.Web;

public sealed class ConversationEventPresentationTests
{
    [Fact]
    public void Group_keeps_replies_errors_and_questions_visible_and_preserves_all_details()
    {
        var at = DateTime.UtcNow;
        StreamEvent[] events =
        [
            new(at, "user", "Investigate the refresh"),
            new(at.AddSeconds(1), "assistant", "Reading the configuration"),
            new(at.AddSeconds(2), "tool_use", "Read", """{"path":"config.json"}"""),
            new(at.AddSeconds(3), "stderr", "Informational provider diagnostic"),
            new(at.AddSeconds(4), "rate_limit_event", "remaining=2"),
            new(at.AddSeconds(5), "error", "Permission denied"),
            new(at.AddSeconds(6), "ask_user_question", "Choose a location"),
            new(at.AddSeconds(7), "tool_use", "Write", """{"path":"output.json"}"""),
            new(at.AddSeconds(8), "result", """{"cost":0.01}"""),
            new(at.AddSeconds(9), "assistant", "Done")
        ];

        var groups = ConversationEventPresentation.Group(events);

        Assert.Equal(events, groups.SelectMany(group => group.Events));
        Assert.Equal(2, groups.Count(group => group.IsActivity));
        Assert.All(groups.Where(group => group.IsActivity),
            group => Assert.DoesNotContain(group.Events, ev => ev.Kind is "error" or "ask_user_question" or "assistant" or "user"));
        Assert.Contains(groups, group => !group.IsActivity && group.Events[0].Kind == "error");
        Assert.Contains(groups, group => !group.IsActivity && group.Events[0].Kind == "ask_user_question");
    }

    [Fact]
    public void Unknown_provider_events_remain_available_in_activity_instead_of_being_dropped()
    {
        StreamEvent[] events = [new(DateTime.UtcNow, "provider_extension", "opaque event", """{"original":"payload"}""")];
        var group = Assert.Single(ConversationEventPresentation.Group(events));
        Assert.True(group.IsActivity);
        Assert.Same(events[0], Assert.Single(group.Events));
        Assert.Empty(ConversationEventPresentation.Group([]));
    }
}
