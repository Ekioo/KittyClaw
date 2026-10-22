using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapMembers(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/members", async (string slug, MemberService ms) =>
            Results.Ok(await ms.ListMembersAsync(slug)))
            .WithTags("Members");

        api.MapPost("/projects/{slug}/members", HandleCreateMember).Produces<KittyClaw.Core.Models.Member>(StatusCodes.Status201Created).WithTags("Members");

        api.MapPatch("/projects/{slug}/members/{memberId:int}", HandleUpdateMember).WithTags("Members");

        api.MapDelete("/projects/{slug}/members/{memberId:int}", HandleDeleteMember).WithTags("Members");

        api.MapGet("/projects/{slug}/mentions/{handle}", async (string slug, string handle, DateTime? since, DateTime? until, TicketService ts) =>
        {
            var tickets = await ts.ListMentionedTicketsAsync(slug, handle, since, until);
            return Results.Ok(tickets);
        }).WithTags("Mentions");
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleCreateMember(string slug, CreateMemberRequest req, MemberService ms, BoardUpdateNotifier notifier)
    {
        var member = await ms.CreateMemberAsync(slug, req.Name);
        notifier.NotifyProjectUpdated(slug);
        return Results.Created($"/api/projects/{slug}/members/{member.Id}", member);
    }

    internal static async Task<IResult> HandleUpdateMember(string slug, int memberId, UpdateMemberRequest req, MemberService ms, BoardUpdateNotifier notifier)
    {
        var member = await ms.UpdateMemberAsync(slug, memberId, req.Name, req.DefaultModel);
        if (member is not null) notifier.NotifyProjectUpdated(slug);
        return member is null ? Results.NotFound() : Results.Ok(member);
    }

    internal static async Task<IResult> HandleDeleteMember(string slug, int memberId, MemberService ms, BoardUpdateNotifier notifier)
    {
        var result = await ms.DeleteMemberAsync(slug, memberId);
        switch (result)
        {
            case DeleteMemberResult.Deleted:
                notifier.NotifyProjectUpdated(slug);
                return Results.NoContent();
            case DeleteMemberResult.ProtectedOwner:
                return Results.Conflict(new { error = "cannot delete owner" });
            default:
                return Results.NotFound();
        }
    }
}
