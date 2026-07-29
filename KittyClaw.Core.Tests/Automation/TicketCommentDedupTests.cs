using System.Text.Json.Nodes;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Non-regression tests for ticket #113: the owner-feedback misfire. The same comment used to
/// re-fire on every poll (up to 8 phantom agent runs) because the consumed-comment state was a
/// shared flat map overwritten wholesale by a non-atomic Load → await → Save cycle, and the
/// urgent signal path never recorded consumption at all.
/// </summary>
public class TicketCommentDedupTests
{
    private sealed record Harness(TicketService Tickets, SessionRegistry Sessions, string Slug, string Workspace);

    private static async Task<Harness> BuildAsync(string dataDir)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("comment-dedup-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        return new Harness(tickets, new SessionRegistry(), project.Slug, workspace);
    }

    private static TriggerContext Context(Harness h, string automationId) => new()
    {
        ProjectSlug = h.Slug,
        WorkspacePath = h.Workspace,
        Automation = new AutomationRule
        {
            Id = automationId,
            Trigger = new TicketCommentAddedTriggerSpec { PollSeconds = 0 },
        },
        Tickets = h.Tickets,
        Members = null!,
        Sessions = h.Sessions,
        Runs = null!,
        Now = DateTime.UtcNow,
    };

    private static TicketCommentAddedTrigger Trigger(params string[] authors) =>
        new(new TicketCommentAddedTriggerSpec { PollSeconds = 0, Authors = authors.ToList() });

    [Fact]
    public async Task SameComment_FiresOncePerPoll_NeverAgain()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var trigger = Trigger("owner");
        var ctx = Context(h, "owner-feedback");
        await trigger.EvaluateAsync(ctx, CancellationToken.None); // silent seed scan

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "feedback", "owner");

        var first = await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None);
        Assert.Single(first);

        // The prod bug: every subsequent poll re-fired the same comment (8 phantom runs).
        for (var i = 0; i < 5; i++)
            Assert.Empty(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task UrgentSignal_ConsumesComment_NextPollDoesNotRefire()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var trigger = Trigger("owner");
        await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None); // seed

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        var comment = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "feedback", "owner");

        // Signal path dispatches immediately…
        Assert.True(trigger.TryHandleExternalSignal(
            new CommentAddedSignal(ticket.Id, comment!.Id, "owner", "feedback"), out var urgent));
        var firing = Assert.Single(urgent);
        // …the engine consumes it at dispatch (what TriggerHandler does post-conditions)…
        await trigger.ConsumeSignalFiringAsync(Context(h, "owner-feedback"), firing);

        // …and the historical urgent+poll double fire is gone.
        Assert.Empty(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task SignalWithFailingConditions_IsNotConsumed_PollRetriesIt()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var trigger = Trigger("owner");
        await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None); // seed

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        var comment = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "feedback", "owner");

        // Signal received, but the engine does NOT consume (conditions failed → no dispatch).
        Assert.True(trigger.TryHandleExternalSignal(
            new CommentAddedSignal(ticket.Id, comment!.Id, "owner", "feedback"), out _));

        // The old behavior consumed at signal time and lost the comment; the poll must retry it.
        Assert.Single(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task SignalConsumption_SurvivesReload()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var trigger = Trigger("owner");
        await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None); // seed

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        var comment = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "feedback", "owner");
        Assert.True(trigger.TryHandleExternalSignal(
            new CommentAddedSignal(ticket.Id, comment!.Id, "owner", "feedback"), out var urgent));
        await trigger.ConsumeSignalFiringAsync(Context(h, "owner-feedback"), urgent[0]);

        // Reload swaps the trigger instance: consumption is persisted, not in-memory.
        var reloaded = Trigger("owner");
        Assert.Empty(await reloaded.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task Consume_WithoutPendingEntry_FallsBackToTicketMaxCommentId()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var seeder = Trigger("owner");
        await seeder.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None); // seed

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "feedback", "owner");

        // A reload between signal and dispatch loses the in-memory pending map: the fallback
        // must resolve the ticket's max comment id so the poll still doesn't double-fire.
        var fresh = Trigger("owner");
        await fresh.ConsumeSignalFiringAsync(Context(h, "owner-feedback"), new TriggerFiring(ticket.Id, null, null));
        Assert.Empty(await fresh.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task TwoAutomations_TrackConsumptionIndependently()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ownerTrigger = Trigger("owner");
        var agentTrigger = Trigger("programmer");
        await ownerTrigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None); // seed
        await agentTrigger.EvaluateAsync(Context(h, "agent-watch"), CancellationToken.None);    // seed

        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "from owner", "owner");
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "from agent", "programmer");

        // The old shared flat map: whichever automation polled first advanced the shared max
        // and hid (or rolled back) the other's comments. Each must now fire exactly once.
        Assert.Single(await ownerTrigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
        Assert.Single(await agentTrigger.EvaluateAsync(Context(h, "agent-watch"), CancellationToken.None));
        Assert.Empty(await ownerTrigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
        Assert.Empty(await agentTrigger.EvaluateAsync(Context(h, "agent-watch"), CancellationToken.None));
    }

    [Fact]
    public async Task StaleWriter_CannotRollBackConsumedState()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var trigger = Trigger("owner");
        await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None); // seed
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "feedback", "owner");
        Assert.Single(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));

        // Simulate the prod interleaving: a stale writer persists an older snapshot (max=0)
        // over the consumed state. The monotonic merge must keep the higher consumed ID —
        // with the old whole-object save this rolled back and re-fired every poll.
        var stale = new TicketCommentAddedTrigger(new TicketCommentAddedTriggerSpec { PollSeconds = 0, Authors = ["ghost"] });
        await stale.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None);

        Assert.Empty(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task FirstScan_SeedsSilently_InsteadOfReplayingHistory()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "historical", "owner");

        var trigger = Trigger("owner");
        // No persisted state at all: the scan must record, not fire, the board's history.
        Assert.Empty(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));

        // A comment arriving after the seed fires normally.
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "new feedback", "owner");
        Assert.Single(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }

    [Fact]
    public async Task LegacyFlatState_SeedsAutomationBucket_WithoutRefireWave()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Todo");
        var consumed = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "already handled", "owner");

        // Pre-#113 installs persisted a shared flat "_lastCommentIds" map.
        h.Sessions.Update(h.Workspace, state =>
            state["_lastCommentIds"] = new JsonObject { [ticket.Id.ToString()] = consumed!.Id });

        var trigger = Trigger("owner");
        Assert.Empty(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));

        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "genuinely new", "owner");
        Assert.Single(await trigger.EvaluateAsync(Context(h, "owner-feedback"), CancellationToken.None));
    }
}
