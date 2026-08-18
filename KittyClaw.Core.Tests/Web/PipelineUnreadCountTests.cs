using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class PipelineUnreadCountTests
{
    private static readonly DateTime Baseline = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EmptyFilter_PreservesUnreadCount()
    {
        var tickets = new[]
        {
            Make(1, "alpha", pipelineId: 1),
            Make(2, "beta", pipelineId: 1),
            Make(3, "other pipeline", pipelineId: 2)
        };

        Assert.Equal(2, Count(tickets, pipelineId: 1, filter: ""));
    }

    [Fact]
    public void Filter_IsAppliedIndependentlyToEveryPipeline()
    {
        var tickets = new[]
        {
            Make(1, "release alpha", pipelineId: 1),
            Make(2, "maintenance", pipelineId: 1),
            Make(3, "release beta", pipelineId: 2),
            Make(4, "unread but viewed", pipelineId: 2)
        };
        var viewed = new Dictionary<int, DateTime> { [4] = Baseline.AddHours(2) };

        Assert.Equal(1, Count(tickets, 1, "release", viewedAt: viewed));
        Assert.Equal(1, Count(tickets, 2, "release", viewedAt: viewed));
    }

    [Fact]
    public void FilterWithNoUnreadMatch_ReturnsZero()
    {
        var tickets = new[] { Make(1, "alpha", pipelineId: 1) };

        Assert.Equal(0, Count(tickets, pipelineId: 1, filter: "missing"));
    }

    [Fact]
    public void CombinedFilter_UsesBoardTicketFilterSemanticsAndLocalizedPriority()
    {
        var tickets = new[]
        {
            Make(1, "deploy service", pipelineId: 1, assignedTo: "alice", priority: TicketPriority.Critical),
            Make(2, "deploy service", pipelineId: 1, assignedTo: "bob", priority: TicketPriority.Critical),
            Make(3, "deploy service", pipelineId: 1, assignedTo: "alice", priority: TicketPriority.NiceToHave)
        };
        string PriorityLabel(TicketPriority priority) =>
            priority == TicketPriority.Critical ? "Critique" : "Souhaitable";

        Assert.Equal(1, Count(tickets, 1, "deploy @alice priority:Critique", PriorityLabel));
    }

    [Fact]
    public void ParentContext_ExcludesRootAndOtherSubKanbans()
    {
        var tickets = new[]
        {
            Make(1, "child", pipelineId: 1, parentId: 42),
            Make(2, "root", pipelineId: 1),
            Make(3, "other child", pipelineId: 1, parentId: 99)
        };

        Assert.Equal(1, Count(tickets, pipelineId: 1, filter: "", parentId: 42));
    }

    private static int Count(
        IEnumerable<TicketSummary> tickets,
        int pipelineId,
        string filter,
        Func<TicketPriority, string>? priorityLabel = null,
        int? parentId = null,
        IReadOnlyDictionary<int, DateTime>? viewedAt = null) =>
        BoardUnreadState.CountPipelineUnread(
            tickets,
            pipelineId,
            parentId,
            filter,
            viewedAt ?? new Dictionary<int, DateTime>(),
            Baseline,
            priorityLabel);

    private static TicketSummary Make(
        int id,
        string title,
        int pipelineId,
        int? parentId = null,
        string? assignedTo = null,
        TicketPriority priority = TicketPriority.NiceToHave) =>
        new(id, title, "", "Todo", priority, 0, assignedTo, "owner",
            Baseline, Baseline.AddHours(1), [], 0, null, parentId, [])
        {
            PipelineId = pipelineId
        };
}
