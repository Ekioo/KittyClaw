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

        api.MapPost("/projects/{slug}/members", async (string slug, CreateMemberRequest req, MemberService ms, BoardUpdateNotifier notifier) =>
        {
            var member = await ms.CreateMemberAsync(slug, req.Name);
            notifier.NotifyProjectUpdated(slug);
            return Results.Created($"/api/projects/{slug}/members/{member.Id}", member);
        }).WithTags("Members");

        api.MapPatch("/projects/{slug}/members/{memberId:int}", async (string slug, int memberId, UpdateMemberRequest req, MemberService ms, BoardUpdateNotifier notifier) =>
        {
            var member = await ms.UpdateMemberAsync(slug, memberId, req.Name);
            if (member is not null) notifier.NotifyProjectUpdated(slug);
            return member is null ? Results.NotFound() : Results.Ok(member);
        }).WithTags("Members");

        api.MapDelete("/projects/{slug}/members/{memberId:int}", async (string slug, int memberId, MemberService ms, BoardUpdateNotifier notifier) =>
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
        }).WithTags("Members");

        api.MapGet("/projects/{slug}/mentions/{handle}", async (string slug, string handle, DateTime? since, DateTime? until, TicketService ts) =>
        {
            var tickets = await ts.ListMentionedTicketsAsync(slug, handle, since, until);
            return Results.Ok(tickets);
        }).WithTags("Mentions");
    }
}
