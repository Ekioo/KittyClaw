using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class MemberServiceTests
{
    private static (MemberService members, TicketService tickets, ProjectService projects, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("member-test").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        return (members, tickets, projects, project.Slug);
    }

    [Fact]
    public async Task DeleteMemberAsync_ExistingNonOwner_ReturnsDeletedAndRemovesRow()
    {
        using var tmp = new TempDir();
        var (members, _, _, slug) = BuildSut(tmp);

        var alice = await members.CreateMemberAsync(slug, "Alice");

        var result = await members.DeleteMemberAsync(slug, alice.Id);

        Assert.Equal(DeleteMemberResult.Deleted, result);
        var all = await members.ListMembersAsync(slug);
        Assert.DoesNotContain(all, m => m.Id == alice.Id);
    }

    [Fact]
    public async Task DeleteMemberAsync_UnknownId_ReturnsNotFound()
    {
        using var tmp = new TempDir();
        var (members, _, _, slug) = BuildSut(tmp);

        var result = await members.DeleteMemberAsync(slug, 9999);

        Assert.Equal(DeleteMemberResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteMemberAsync_OwnerSlug_ReturnsProtectedOwner()
    {
        using var tmp = new TempDir();
        var (members, _, _, slug) = BuildSut(tmp);

        var all = await members.ListMembersAsync(slug);
        var owner = all.Single(m => m.Slug == "owner");

        var result = await members.DeleteMemberAsync(slug, owner.Id);

        Assert.Equal(DeleteMemberResult.ProtectedOwner, result);
        var afterAll = await members.ListMembersAsync(slug);
        Assert.Contains(afterAll, m => m.Slug == "owner");
    }

    [Fact]
    public async Task DeleteMemberAsync_ClearsAssignedToOnTickets()
    {
        using var tmp = new TempDir();
        var (members, tickets, _, slug) = BuildSut(tmp);

        var alice = await members.CreateMemberAsync(slug, "Alice");
        var bob = await members.CreateMemberAsync(slug, "Bob");
        var bobTicket = await tickets.CreateTicketAsync(slug, "for bob", assignedTo: bob.Slug);
        var aliceTicket = await tickets.CreateTicketAsync(slug, "for alice", assignedTo: alice.Slug);

        var result = await members.DeleteMemberAsync(slug, bob.Id);

        Assert.Equal(DeleteMemberResult.Deleted, result);
        var refreshedBob = await tickets.GetTicketAsync(slug, bobTicket.Id);
        var refreshedAlice = await tickets.GetTicketAsync(slug, aliceTicket.Id);
        Assert.NotNull(refreshedBob);
        Assert.Null(refreshedBob!.AssignedTo);
        Assert.NotNull(refreshedAlice);
        Assert.Equal(alice.Slug, refreshedAlice!.AssignedTo);
    }

    [Fact]
    public async Task DeleteMemberAsync_NoAssignedTickets_StillReturnsDeleted()
    {
        using var tmp = new TempDir();
        var (members, tickets, _, slug) = BuildSut(tmp);

        var alice = await members.CreateMemberAsync(slug, "Alice");
        await tickets.CreateTicketAsync(slug, "unassigned");

        var result = await members.DeleteMemberAsync(slug, alice.Id);

        Assert.Equal(DeleteMemberResult.Deleted, result);
    }

    [Fact]
    public async Task DeleteMemberAsync_TwoMembersSameSlugConflictGuard()
    {
        using var tmp = new TempDir();
        var (members, tickets, _, slug) = BuildSut(tmp);

        var a1 = await members.CreateMemberAsync(slug, "Dup");
        var a2 = await members.CreateMemberAsync(slug, "Dup");
        Assert.Equal(a1.Slug, a2.Slug);

        var t = await tickets.CreateTicketAsync(slug, "assigned to dup", assignedTo: a1.Slug);

        var result = await members.DeleteMemberAsync(slug, a1.Id);

        Assert.Equal(DeleteMemberResult.Deleted, result);
        var refreshed = await tickets.GetTicketAsync(slug, t.Id);
        Assert.NotNull(refreshed);
        Assert.Null(refreshed!.AssignedTo);
        var all = await members.ListMembersAsync(slug);
        Assert.Contains(all, m => m.Id == a2.Id);
    }
}
