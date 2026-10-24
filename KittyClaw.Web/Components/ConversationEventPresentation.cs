using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Components;

public sealed record ConversationEventGroup(bool IsActivity, IReadOnlyList<StreamEvent> Events);

public static class ConversationEventPresentation
{
    public static bool IsActivity(string kind) => kind is not
        ("assistant" or "user" or "steer" or "error" or "ask_user_question");

    // Group adjacent technical events only; replies and failures keep their chronological position.
    public static IReadOnlyList<ConversationEventGroup> Group(IEnumerable<StreamEvent> events)
    {
        var groups = new List<ConversationEventGroup>();
        List<StreamEvent>? activity = null;
        foreach (var ev in events)
        {
            if (IsActivity(ev.Kind))
            {
                if (activity is null)
                {
                    activity = [];
                    groups.Add(new(true, activity));
                }
                activity.Add(ev);
            }
            else
            {
                activity = null;
                groups.Add(new(false, new[] { ev }));
            }
        }
        return groups;
    }
}
