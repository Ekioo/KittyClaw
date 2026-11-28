namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    public static void MapTodoApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        MapAgentAccess(api);

        MapColumns(api);
        MapPipelines(api);
        MapWorkflowMigrations(api);
        MapProjects(api);
        MapProjectSecrets(api);
        MapWorktreeMerges(api);
        MapTickets(api);
        MapTicketImprovement(api);
        MapProjectLabels(api);
        MapTicketLabels(api);
        MapTicketReorder(api);
        MapTicketEvidence(api);
        MapMembers(api);
        MapBrowse(api);
        MapSkills(api);
        MapColumnProcessors(api);
        MapColumnScheduledTasks(api);
        MapColumnExecutions(api);
        MapTicketQuestions(api);
        MapEngine(api);
        MapRuns(api);
        MapChat(api);
        MapImages(api);
        MapDashboard(api);
        MapOllama(api);
        MapGrok(api);
        MapCodex(api);
        MapMistral(api);
        MapCopilot(api);
        MapDeepSeek(api);
        MapApprovals(api);
        MapBoundaryObservations(api);
        MapActivationMetrics(api);
    }
}
