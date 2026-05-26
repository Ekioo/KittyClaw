namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    public static void MapTodoApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapColumns(api);
        MapProjects(api);
        MapTickets(api);
        MapProjectLabels(api);
        MapTicketLabels(api);
        MapTicketReorder(api);
        MapMembers(api);
        MapBrowse(api);
        MapSkills(api);
        MapAutomations(api);
        MapRuns(api);
        MapChat(api);
        MapImages(api);
        MapDashboard(api);
    }
}
