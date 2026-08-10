namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    public static void MapTodoApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapColumns(api);
        MapPipelines(api);
        MapWorkflowMigrations(api);
        MapProjects(api);
        MapProjectSecrets(api);
        MapWorktreeMerges(api);
        MapTickets(api);
        MapProjectLabels(api);
        MapTicketLabels(api);
        MapTicketReorder(api);
        MapTicketEvidence(api);
        MapMembers(api);
        MapBrowse(api);
        MapSkills(api);
        MapColumnProcessors(api);
        MapColumnExecutions(api);
        MapAutomations(api);
        MapEngine(api);
        MapRuns(api);
        MapChat(api);
        MapImages(api);
        MapDashboard(api);
        MapOllama(api);
        MapGrok(api);
        MapCodex(api);
        MapMistral(api);
        MapDeepSeek(api);
        MapApprovals(api);
        MapBoundaryObservations(api);
        MapActivationMetrics(api);
    }
}
