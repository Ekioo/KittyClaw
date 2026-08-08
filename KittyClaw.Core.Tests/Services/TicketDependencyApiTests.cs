using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Integration tests for AddDependencyAsync / RemoveDependencyAsync validation logic
/// and for the BlockedBy / Blocks fields exposed on GetTicketAsync.
/// </summary>
public sealed class TicketDependencyApiTests
{
    private static async Task<(TicketService svc, string slug)> BuildSutAsync(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("dep-api-test");
        var members = new MemberService(projects);
        return (new TicketService(projects, members), project.Slug);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddDependency_ValidEdge_ReturnsPersistedDependency()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");

        var dep = await svc.AddDependencyAsync(slug, t1.Id, t2.Id);

        Assert.Equal(t1.Id, dep.BlockedTicketId);
        Assert.Equal(t2.Id, dep.BlocksTicketId);
        Assert.True(dep.Id > 0);
    }

    [Fact]
    public async Task RemoveDependency_ExistingEdge_ReturnsTrue()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");
        var dep = await svc.AddDependencyAsync(slug, t1.Id, t2.Id);

        var removed = await svc.RemoveDependencyAsync(slug, t1.Id, dep.Id);

        Assert.True(removed);
    }

    [Fact]
    public async Task RemoveDependency_NonexistentEdge_ReturnsFalse()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");

        var removed = await svc.RemoveDependencyAsync(slug, t1.Id, 9999);

        Assert.False(removed);
    }

    [Fact]
    public async Task RemoveDependency_WrongTicket_ReturnsFalse()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");
        var t3 = await svc.CreateTicketAsync(slug, "C");
        var dep = await svc.AddDependencyAsync(slug, t1.Id, t2.Id);

        // depId belongs to t1, not t3 — should not match
        var removed = await svc.RemoveDependencyAsync(slug, t3.Id, dep.Id);

        Assert.False(removed);
    }

    // ------------------------------------------------------------------
    // GetTicketAsync exposes BlockedBy and Blocks
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetTicket_PopulatesBlockedBy_AndBlocks()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");
        await svc.AddDependencyAsync(slug, t1.Id, t2.Id); // t1 blocked by t2

        var fetched1 = await svc.GetTicketAsync(slug, t1.Id);
        var fetched2 = await svc.GetTicketAsync(slug, t2.Id);

        Assert.NotNull(fetched1);
        Assert.NotNull(fetched2);
        Assert.Single(fetched1!.BlockedBy);
        Assert.Equal(t2.Id, fetched1.BlockedBy[0].TicketId);
        Assert.Empty(fetched1.Blocks);
        Assert.Single(fetched2!.Blocks);
        Assert.Equal(t1.Id, fetched2.Blocks[0].TicketId);
        Assert.Empty(fetched2.BlockedBy);
    }

    // ------------------------------------------------------------------
    // Validation: self-reference
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddDependency_SelfReference_ThrowsWithSelfReferenceReason()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, t1.Id, t1.Id));
        Assert.Equal("self_reference", ex.Reason);
    }

    // ------------------------------------------------------------------
    // Validation: missing ticket
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddDependency_MissingBlockedTicket_ThrowsMissingTicket()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t2 = await svc.CreateTicketAsync(slug, "B");

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, 9999, t2.Id));
        Assert.Equal("missing_ticket", ex.Reason);
    }

    [Fact]
    public async Task AddDependency_MissingBlockerTicket_ThrowsMissingTicket()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, t1.Id, 9999));
        Assert.Equal("missing_ticket", ex.Reason);
    }

    // ------------------------------------------------------------------
    // Validation: duplicate edge
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddDependency_DuplicateEdge_ThrowsDuplicateEdge()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");
        await svc.AddDependencyAsync(slug, t1.Id, t2.Id);

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, t1.Id, t2.Id));
        Assert.Equal("duplicate_edge", ex.Reason);
    }

    // ------------------------------------------------------------------
    // Validation: cycle detection
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddDependency_DirectCycle_ThrowsCycle()
    {
        // A blocked by B, then B blocked by A → direct cycle
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var tA = await svc.CreateTicketAsync(slug, "A");
        var tB = await svc.CreateTicketAsync(slug, "B");
        await svc.AddDependencyAsync(slug, tA.Id, tB.Id); // A ← B

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, tB.Id, tA.Id)); // B ← A (cycle)
        Assert.Equal("cycle", ex.Reason);
    }

    [Fact]
    public async Task AddDependency_TransitiveCycle_ThrowsCycle()
    {
        // A ← B ← C, then C ← A → transitive cycle
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var tA = await svc.CreateTicketAsync(slug, "A");
        var tB = await svc.CreateTicketAsync(slug, "B");
        var tC = await svc.CreateTicketAsync(slug, "C");
        await svc.AddDependencyAsync(slug, tA.Id, tB.Id); // A blocked by B
        await svc.AddDependencyAsync(slug, tB.Id, tC.Id); // B blocked by C

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, tC.Id, tA.Id)); // C ← A (cycle)
        Assert.Equal("cycle", ex.Reason);
    }

    [Fact]
    public async Task AddDependency_NoCycle_Succeeds()
    {
        // A ← B ← C, then A ← C is fine (diamond, not a cycle)
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        var tA = await svc.CreateTicketAsync(slug, "A");
        var tB = await svc.CreateTicketAsync(slug, "B");
        var tC = await svc.CreateTicketAsync(slug, "C");
        await svc.AddDependencyAsync(slug, tA.Id, tB.Id);
        await svc.AddDependencyAsync(slug, tB.Id, tC.Id);

        var dep = await svc.AddDependencyAsync(slug, tA.Id, tC.Id); // diamond — valid

        Assert.Equal(tA.Id, dep.BlockedTicketId);
        Assert.Equal(tC.Id, dep.BlocksTicketId);
    }

    // ------------------------------------------------------------------
    // Validation: incompatible state (Done/terminal column)
    // ------------------------------------------------------------------

    [Fact]
    public async Task AddDependency_BlockedTicketInSuccessColumn_ThrowsIncompatibleState()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSutAsync(tmp);
        // Create a ticket in the default "Done" column (role = Success).
        // CreateTicketAsync initializes board columns on first use, so "Done" exists.
        var tDone = await svc.CreateTicketAsync(slug, "Done ticket", status: "Done");
        var tB = await svc.CreateTicketAsync(slug, "B");

        var ex = await Assert.ThrowsAsync<DependencyValidationException>(
            () => svc.AddDependencyAsync(slug, tDone.Id, tB.Id));
        Assert.Equal("incompatible_state", ex.Reason);
    }
}
