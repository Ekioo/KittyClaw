using System.ComponentModel;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KittyClaw.Web.Api;

/// <summary>Management and observability tools sharing the REST application handlers.</summary>
[McpServerToolType]
public sealed class McpManagementTools(IServiceProvider services, ProjectService projects)
{
    [McpServerTool(Name = "save_automations", Destructive = true)]
    [Description("Create, edit or enable automations and reload the engine. Pass baseStamp from get_automations to preserve concurrent edits. Enabled rules may launch agents. Trigger, condition and action objects require their type discriminator.")]
    public Task<JsonElement> SaveAutomations(
        [Description("Project slug from list_projects.")] string project,
        [Description("Configuration and optional baseStamp from get_automations.")] SaveAutomationsRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleSaveAutomations(project, request,
            services.GetRequiredService<AutomationStore>(), services.GetRequiredService<AutomationEngine>()));

    [McpServerTool(Name = "update_ticket", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Update ticket fields together, including status and assignee. Use expectedStatus to detect concurrent transitions.")]
    public Task<JsonElement> UpdateTicket(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] UpdateTicketRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateTicket(project, id, request, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "schedule_ticket", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Schedule a ticket for a future time and target status.")]
    public Task<JsonElement> ScheduleTicket(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] ScheduleTicketRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleScheduleTicket(project, id, request, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "delete_ticket", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Permanently delete a ticket.")]
    public Task<JsonElement> DeleteTicket(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteTicket(project, id, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "set_ticket_parent", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Attach a ticket to a parent ticket, validating the hierarchy.")]
    public Task<JsonElement> SetTicketParent(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] SetParentRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleSetTicketParent(project, id, request, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "remove_ticket_parent", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Detach a ticket from its parent.")]
    public Task<JsonElement> RemoveTicketParent(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id) =>
        ExecuteAsync(project, () => Endpoints.HandleRemoveTicketParent(project, id, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "add_ticket_dependency", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Add a blocking dependency, validating cycles and ticket ownership.")]
    public Task<JsonElement> AddTicketDependency(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] AddDependencyRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleAddTicketDependency(project, id, request, services.GetRequiredService<TicketService>()));

    [McpServerTool(Name = "remove_ticket_dependency", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Remove a dependency edge by its dependency id.")]
    public Task<JsonElement> RemoveTicketDependency(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Dependency edge id, not the blocking ticket id.")] int depId) =>
        ExecuteAsync(project, () => Endpoints.HandleRemoveTicketDependency(project, id, depId, services.GetRequiredService<TicketService>()));

    [McpServerTool(Name = "update_ticket_comment", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Edit an existing ticket comment.")]
    public Task<JsonElement> UpdateTicketComment(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Comment id from get_ticket.")] int commentId,
        [Description("Fields for this operation.")] UpdateCommentRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateTicketComment(project, id, commentId, request, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "delete_ticket_comment", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Permanently delete a ticket comment.")]
    public Task<JsonElement> DeleteTicketComment(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Comment id from get_ticket.")] int commentId) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteTicketComment(project, id, commentId, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "reorder_ticket", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Change ticket position and status using workflow routing validation.")]
    public Task<JsonElement> ReorderTicket(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] ReorderTicketRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleReorderTicket(project, id, request, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "transfer_ticket", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Transfer a complete ticket tree and history to another project.")]
    public Task<JsonElement> TransferTicket(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] TransferTicketRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleTransferTicket(project, id, request, services.GetRequiredService<TicketTransferService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "get_ticket_evidence", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read ticket evidence with current staleness indicators.")]
    public Task<JsonElement> GetTicketEvidence(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id) =>
        ExecuteAsync(project, () => Endpoints.HandleGetTicketEvidence(project, id, services.GetRequiredService<EvidenceStore>(), services.GetRequiredService<TicketService>()));

    [McpServerTool(Name = "get_ticket_brief", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read the decision brief composed from ticket evidence.")]
    public Task<JsonElement> GetTicketBrief(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id) =>
        ExecuteAsync(project, () => Endpoints.HandleGetTicketBrief(project, id, services.GetRequiredService<EvidenceStore>(), services.GetRequiredService<TicketService>()));

    [McpServerTool(Name = "create_project", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Create a KittyClaw project.")]
    public Task<JsonElement> CreateProject(
        [Description("Fields for this operation.")] CreateProjectRequest request) =>
        ExecuteAsync(null, () => Endpoints.HandleCreateProject(request, services.GetRequiredService<ProjectService>()));

    [McpServerTool(Name = "get_project", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read project configuration.")]
    public Task<JsonElement> GetProject(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Endpoints.HandleGetProject(project, services.GetRequiredService<ProjectService>()));

    [McpServerTool(Name = "update_project", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Configure workspace, fallback model, worktrees and runtime boundary enforcement.")]
    public Task<JsonElement> UpdateProject(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] UpdateProjectRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateProject(project, request, services.GetRequiredService<ProjectService>()));

    [McpServerTool(Name = "toggle_project_pause", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Toggle project pause. Pausing cancels and waits for active runs; resuming permits dispatch.")]
    public Task<JsonElement> ToggleProjectPause(
        [Description("Project slug from list_projects.")] string project,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(project, () => Endpoints.HandleToggleProjectPause(project, services.GetRequiredService<ProjectService>(), services.GetRequiredService<AgentRunRegistry>(), cancellationToken));

    [McpServerTool(Name = "configure_local_model", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Configure the project local model endpoint and name.")]
    public Task<JsonElement> ConfigureLocalModel(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] SaveLocalModelConfigRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleConfigureLocalModel(project, request, services.GetRequiredService<ProjectService>()));

    [McpServerTool(Name = "create_member", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Create a project member.")]
    public Task<JsonElement> CreateMember(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] CreateMemberRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleCreateMember(project, request, services.GetRequiredService<MemberService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "update_member", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Update member name and default model.")]
    public Task<JsonElement> UpdateMember(
        [Description("Project slug from list_projects.")] string project,
        [Description("Member id from board_overview.")] int memberId,
        [Description("Fields for this operation.")] UpdateMemberRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateMember(project, memberId, request, services.GetRequiredService<MemberService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "delete_member", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Delete a project member. The owner is protected.")]
    public Task<JsonElement> DeleteMember(
        [Description("Project slug from list_projects.")] string project,
        [Description("Member id from board_overview.")] int memberId) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteMember(project, memberId, services.GetRequiredService<MemberService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "list_columns", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List columns, optionally within a pipeline.")]
    public Task<JsonElement> ListColumns(
        [Description("Project slug from list_projects.")] string project,
        [Description("Pipeline id from list_pipelines.")] int? pipelineId = null) =>
        ExecuteAsync(project, () => Endpoints.HandleListColumns(project, pipelineId, services.GetRequiredService<ColumnService>()));

    [McpServerTool(Name = "create_column", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Create a workflow column with role and user guidance.")]
    public Task<JsonElement> CreateColumn(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] CreateColumnRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleCreateColumn(project, request, services.GetRequiredService<ColumnService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "update_column", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Update column name, color, role or guidance.")]
    public Task<JsonElement> UpdateColumn(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column id from list_columns.")] int columnId,
        [Description("Fields for this operation.")] UpdateColumnRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateColumn(project, columnId, request, services.GetRequiredService<ColumnService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "delete_column", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Delete a column and move its tickets to the specified status.")]
    public Task<JsonElement> DeleteColumn(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column id from list_columns.")] int columnId,
        [Description("Target status for tickets from the deleted column.")] string moveTicketsTo) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteColumn(project, columnId, moveTicketsTo, services.GetRequiredService<ColumnService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "reorder_column", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Reorder a workflow column.")]
    public Task<JsonElement> ReorderColumn(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] ReorderColumnRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleReorderColumn(project, request, services.GetRequiredService<ColumnService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "list_pipelines", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List project pipelines.")]
    public Task<JsonElement> ListPipelines(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Endpoints.HandleListPipelines(project, services.GetRequiredService<PipelineService>()));

    [McpServerTool(Name = "create_pipeline", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Create a project pipeline.")]
    public Task<JsonElement> CreatePipeline(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] CreatePipelineRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleCreatePipeline(project, request, services.GetRequiredService<PipelineService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "update_pipeline", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Rename a project pipeline.")]
    public Task<JsonElement> UpdatePipeline(
        [Description("Project slug from list_projects.")] string project,
        [Description("Pipeline id from list_pipelines.")] int pipelineId,
        [Description("Fields for this operation.")] UpdatePipelineRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdatePipeline(project, pipelineId, request, services.GetRequiredService<PipelineService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "get_column_processor", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read a column agent processor configuration.")]
    public Task<JsonElement> GetColumnProcessor(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column id from list_columns.")] int columnId) =>
        ExecuteAsync(project, () => Endpoints.HandleGetColumnProcessor(project, columnId, services.GetRequiredService<ColumnProcessorService>()));

    [McpServerTool(Name = "save_column_processor", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Configure a column in temporal order: entry gates are separate column settings; choose disabled, agent, JEV triage or JEV evidence review, then configure its applicable preparation, execution, final assessments/evidence, routing and actions. JEV profile selection precedes dispatch; JEV post-agent routing follows the saved result. In both JEV-only modes explicitly choose jevFallbackToAgent for uncertainty or unavailability; new definitions should send false unless an agent fallback is intended. Read existing values and preserve unspecified settings on updates. Scheduled tasks remain independent. Enabled processors can dispatch agents.")]
    public Task<JsonElement> SaveColumnProcessor(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column id from list_columns.")] int columnId,
        [Description("Processor fields, including the exclusive mode flags and explicit JEV-only fallback policy. Preserve saved values when updating because omitted fields may have defaults.")] SaveColumnProcessorRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleSaveColumnProcessor(project, columnId, request, services.GetRequiredService<ColumnProcessorService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "delete_column_processor", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Delete a column processor.")]
    public Task<JsonElement> DeleteColumnProcessor(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column id from list_columns.")] int columnId) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteColumnProcessor(project, columnId, services.GetRequiredService<ColumnProcessorService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "list_column_executions", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List column executions, optionally for one ticket.")]
    public Task<JsonElement> ListColumnExecutions(
        [Description("Project slug from list_projects.")] string project,
        [Description("Optional ticket id filter.")] int? ticketId = null) =>
        ExecuteAsync(project, () => Endpoints.HandleListColumnExecutions(project, ticketId, services.GetRequiredService<ColumnExecutionService>()));

    [McpServerTool(Name = "retry_column_execution", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Retry a column execution and signal the processing engine. May launch an agent.")]
    public Task<JsonElement> RetryColumnExecution(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column execution id from list_column_executions.")] string executionId) =>
        ExecuteAsync(project, () => Endpoints.HandleRetryColumnExecution(project, executionId, services.GetRequiredService<ColumnExecutionService>(), services.GetRequiredService<ColumnProcessingEngine>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "cancel_column_execution", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Cancel a column execution.")]
    public Task<JsonElement> CancelColumnExecution(
        [Description("Project slug from list_projects.")] string project,
        [Description("Column execution id from list_column_executions.")] string executionId) =>
        ExecuteAsync(project, () => Endpoints.HandleCancelColumnExecution(project, executionId, services.GetRequiredService<ColumnExecutionService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "get_automations", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read automation configuration and its file stamp.")]
    public Task<JsonElement> GetAutomations(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Endpoints.HandleGetAutomations(project, services.GetRequiredService<AutomationStore>()));

    [McpServerTool(Name = "disable_automation", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Disable an automation and reload the engine; report rejected reloads.")]
    public Task<JsonElement> DisableAutomation(
        [Description("Project slug from list_projects.")] string project,
        [Description("Automation id from get_automations.")] string automationId) =>
        ExecuteAsync(project, () => Endpoints.HandleDisableAutomation(project, automationId, services.GetRequiredService<AutomationStore>(), services.GetRequiredService<AutomationEngine>()));

    [McpServerTool(Name = "delete_automation", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Delete an automation and reload the engine; report rejected reloads.")]
    public Task<JsonElement> DeleteAutomation(
        [Description("Project slug from list_projects.")] string project,
        [Description("Automation id from get_automations.")] string automationId) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteAutomation(project, automationId, services.GetRequiredService<AutomationStore>(), services.GetRequiredService<AutomationEngine>()));

    [McpServerTool(Name = "get_ticket_automation_queue", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read the automation queue for a ticket.")]
    public Task<JsonElement> GetTicketAutomationQueue(
        [Description("Project slug from list_projects.")] string project,
        [Description("Optional ticket id filter.")] int ticketId) =>
        ExecuteAsync(project, () => Endpoints.HandleGetTicketAutomationQueue(project, ticketId, services.GetRequiredService<AutomationQueueStore>()));

    [McpServerTool(Name = "list_runs", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List project agent runs with status and cost. Defaults to active runs; includeCompleted includes retained history. Filter by ticketId and bound results with limit (1-500, default 100).")]
    public Task<JsonElement> ListRuns(
        [Description("Project slug from list_projects.")] string project,
        [Description("Include retained completed, stopped and failed runs.")] bool? includeCompleted = null,
        [Description("Optional ticket filter.")] int? ticketId = null,
        [Description("Maximum results, between 1 and 500. Defaults to 100.")] int? limit = 100) =>
        ExecuteAsync(project, () => Task.FromResult(Endpoints.HandleListRuns(project,
            services.GetRequiredService<AgentRunRegistry>(), includeCompleted, ticketId, limit ?? 100)));

    [McpServerTool(Name = "get_run", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read a run, token usage, cost and buffered log events.")]
    public Task<JsonElement> GetRun(
        [Description("Project slug from list_projects.")] string project,
        [Description("Agent run id from list_runs.")] string runId) =>
        ExecuteAsync(project, () => Task.FromResult(Endpoints.HandleGetRun(project, runId, services.GetRequiredService<AgentRunRegistry>())));

    [McpServerTool(Name = "steer_run", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Send instructions and optional images to an active agent run.")]
    public Task<JsonElement> SteerRun(
        [Description("Project slug from list_projects.")] string project,
        [Description("Agent run id from list_runs.")] string runId,
        [Description("Fields for this operation.")] SteerRunRequest request,
        [Description("Optional model for the steering message.")] string? model = null) =>
        ExecuteAsync(project, () => Endpoints.HandleSteerRun(project, runId, request, services.GetRequiredService<AgentRunRegistry>(), services.GetRequiredService<ChatService>(), model));

    [McpServerTool(Name = "stop_run", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Cancel an agent run and wait for it to stop.")]
    public Task<JsonElement> StopRun(
        [Description("Project slug from list_projects.")] string project,
        [Description("Agent run id from list_runs.")] string runId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(project, () => Endpoints.HandleStopRun(project, runId, services.GetRequiredService<AgentRunRegistry>(), cancellationToken));

    [McpServerTool(Name = "retry_run", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Launch a new agent run from a finished run, respecting concurrency groups. Consumes provider resources.")]
    public Task<JsonElement> RetryRun(
        [Description("Project slug from list_projects.")] string project,
        [Description("Agent run id from list_runs.")] string runId) =>
        ExecuteAsync(project, () => Endpoints.HandleRetryRun(project, runId, services.GetRequiredService<AgentRunRegistry>(), services.GetRequiredService<ProjectService>(), services.GetRequiredService<TicketService>(), services.GetRequiredService<AgentRunner>()));

    [McpServerTool(Name = "get_run_evidence", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read run evidence with current staleness indicators.")]
    public Task<JsonElement> GetRunEvidence(
        [Description("Project slug from list_projects.")] string project,
        [Description("Agent run id from list_runs.")] string runId) =>
        ExecuteAsync(project, () => Task.FromResult(Endpoints.HandleGetRunEvidence(project, runId, services.GetRequiredService<EvidenceStore>(), services.GetRequiredService<AgentRunRegistry>())));

    [McpServerTool(Name = "list_concurrency_groups", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read active concurrency locks and their holders.")]
    public Task<JsonElement> ListConcurrencyGroups(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Task.FromResult(Endpoints.HandleListConcurrencyGroups(project, services.GetRequiredService<AgentRunRegistry>())));

    [McpServerTool(Name = "list_approval_requests", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List project approval requests, optionally filtered by run, ticket or provider.")]
    public Task<JsonElement> ListApprovalRequests(
        [Description("Project slug from list_projects.")] string project,
        [Description("Agent run id from list_runs.")] string? runId = null,
        [Description("Optional ticket id filter.")] int? ticketId = null,
        [Description("provider")] string? provider = null) =>
        ExecuteAsync(project, () => Endpoints.HandleListApprovalRequests(project, runId, ticketId, provider, services.GetRequiredService<ApprovalRegistryService>()));

    [McpServerTool(Name = "list_approval_decisions", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List immutable approval decisions, optionally for one request.")]
    public Task<JsonElement> ListApprovalDecisions(
        [Description("Project slug from list_projects.")] string project,
        [Description("Request id.")] string? requestId = null) =>
        ExecuteAsync(project, () => Endpoints.HandleListApprovalDecisions(project, requestId, services.GetRequiredService<ApprovalRegistryService>()));

    [McpServerTool(Name = "list_approval_receipts", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List approval enforcement receipts.")]
    public Task<JsonElement> ListApprovalReceipts(
        [Description("Project slug from list_projects.")] string project,
        [Description("Request id.")] string? requestId = null) =>
        ExecuteAsync(project, () => Endpoints.HandleListApprovalReceipts(project, requestId, services.GetRequiredService<ApprovalRegistryService>()));

    [McpServerTool(Name = "decide_approval", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Record an allow or deny decision using the approval workflow. Requires explicit human authorization for the decision.")]
    public Task<JsonElement> DecideApproval(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] ApprovalDecisionInput request) =>
        ExecuteAsync(project, () => Endpoints.HandleDecideApproval(project, request, services.GetRequiredService<ApprovalWorkflowService>()));

    [McpServerTool(Name = "get_runtime_capabilities", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read supported provider and runtime boundary enforcement capabilities.")]
    public Task<JsonElement> GetRuntimeCapabilities() =>
        ExecuteAsync(null, () => Task.FromResult(Endpoints.HandleGetRuntimeCapabilities()));

    [McpServerTool(Name = "list_worktree_merges", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List worktree integration requests and synchronization state.")]
    public Task<JsonElement> ListWorktreeMerges(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Endpoints.HandleListWorktreeMerges(project, services.GetRequiredService<WorktreeMergeQueueService>()));

    [McpServerTool(Name = "get_worktree_merge", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read a worktree integration request.")]
    public Task<JsonElement> GetWorktreeMerge(
        [Description("Project slug from list_projects.")] string project,
        [Description("Request id.")] long requestId) =>
        ExecuteAsync(project, () => Endpoints.HandleGetWorktreeMerge(project, requestId, services.GetRequiredService<WorktreeMergeQueueService>()));

    [McpServerTool(Name = "enqueue_worktree_merge", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Queue integration of a ticket worktree.")]
    public Task<JsonElement> EnqueueWorktreeMerge(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] EnqueueWorktreeMergeRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(project, () => Endpoints.HandleEnqueueWorktreeMerge(project, request, services.GetRequiredService<WorktreeMergeQueueService>(), cancellationToken));

    [McpServerTool(Name = "process_next_worktree_merge", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Process the next queued worktree integration, including recovery. Modifies repository state.")]
    public Task<JsonElement> ProcessNextWorktreeMerge(
        [Description("Project slug from list_projects.")] string project,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(project, () => Endpoints.HandleProcessNextWorktreeMerge(project, services.GetRequiredService<WorktreeMergeQueueService>(), cancellationToken));

    [McpServerTool(Name = "resume_worktree_merge", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Resume a worktree integration after resolution. Modifies repository state.")]
    public Task<JsonElement> ResumeWorktreeMerge(
        [Description("Project slug from list_projects.")] string project,
        [Description("Request id.")] long requestId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(project, () => Endpoints.HandleResumeWorktreeMerge(project, requestId, services.GetRequiredService<WorktreeMergeQueueService>(), cancellationToken));

    [McpServerTool(Name = "retry_worktree_synchronization", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Retry synchronization of an eligible worktree integration.")]
    public Task<JsonElement> RetryWorktreeSynchronization(
        [Description("Project slug from list_projects.")] string project,
        [Description("Request id.")] long requestId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(project, () => Endpoints.HandleRetryWorktreeSynchronization(project, requestId, services.GetRequiredService<WorktreeMergeQueueService>(), cancellationToken));

    [McpServerTool(Name = "list_labels", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List project labels.")]
    public Task<JsonElement> ListLabels(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Endpoints.HandleListLabels(project, services.GetRequiredService<LabelService>()));

    [McpServerTool(Name = "create_label", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Create a project label.")]
    public Task<JsonElement> CreateLabel(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] CreateLabelRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleCreateLabel(project, request, services.GetRequiredService<LabelService>()));

    [McpServerTool(Name = "update_label", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Update a project label.")]
    public Task<JsonElement> UpdateLabel(
        [Description("Project slug from list_projects.")] string project,
        [Description("labelId")] int labelId,
        [Description("Fields for this operation.")] UpdateLabelRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateLabel(project, labelId, request, services.GetRequiredService<LabelService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "delete_label", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Delete a project label.")]
    public Task<JsonElement> DeleteLabel(
        [Description("Project slug from list_projects.")] string project,
        [Description("labelId")] int labelId) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteLabel(project, labelId, services.GetRequiredService<LabelService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "patch_ticket_labels", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Add or remove ticket labels by name without replacing concurrent changes.")]
    public Task<JsonElement> PatchTicketLabels(
        [Description("Project slug from list_projects.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Fields for this operation.")] PatchTicketLabelsRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandlePatchTicketLabels(project, id, request, services.GetRequiredService<TicketService>(), services.GetRequiredService<BoardUpdateNotifier>()));

    [McpServerTool(Name = "list_project_skills", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List project skills available to processors.")]
    public Task<JsonElement> ListProjectSkills(
        [Description("Project slug from list_projects.")] string project) =>
        ExecuteAsync(project, () => Endpoints.HandleListProjectSkills(project, services.GetRequiredService<ProjectSkillService>()));

    [McpServerTool(Name = "get_project_skill", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read a project skill instructions.")]
    public Task<JsonElement> GetProjectSkill(
        [Description("Project slug from list_projects.")] string project,
        [Description("Project skill slug from list_project_skills.")] string skillSlug) =>
        ExecuteAsync(project, () => Endpoints.HandleGetProjectSkill(project, skillSlug, services.GetRequiredService<ProjectSkillService>()));

    [McpServerTool(Name = "create_project_skill", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Create reusable project skill instructions.")]
    public Task<JsonElement> CreateProjectSkill(
        [Description("Project slug from list_projects.")] string project,
        [Description("Fields for this operation.")] CreateProjectSkillRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleCreateProjectSkill(project, request, services.GetRequiredService<ProjectSkillService>()));

    [McpServerTool(Name = "update_project_skill", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Update reusable project skill instructions.")]
    public Task<JsonElement> UpdateProjectSkill(
        [Description("Project slug from list_projects.")] string project,
        [Description("Project skill slug from list_project_skills.")] string skillSlug,
        [Description("Fields for this operation.")] UpdateProjectSkillRequest request) =>
        ExecuteAsync(project, () => Endpoints.HandleUpdateProjectSkill(project, skillSlug, request, services.GetRequiredService<ProjectSkillService>()));

    [McpServerTool(Name = "delete_project_skill", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Delete a reusable project skill.")]
    public Task<JsonElement> DeleteProjectSkill(
        [Description("Project slug from list_projects.")] string project,
        [Description("Project skill slug from list_project_skills.")] string skillSlug) =>
        ExecuteAsync(project, () => Endpoints.HandleDeleteProjectSkill(project, skillSlug, services.GetRequiredService<ProjectSkillService>()));

    [McpServerTool(Name = "get_engine_health", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read automation engine health and overdue work across projects.")]
    public Task<JsonElement> GetEngineHealth() =>
        ExecuteAsync(null, () => Endpoints.HandleGetEngineHealth(services.GetRequiredService<ProjectService>(), services.GetRequiredService<AutomationEngine>()));

    private async Task<JsonElement> ExecuteAsync(string? project, Func<Task<IResult>> action)
    {
        if (project is not null && await projects.GetProjectAsync(project) is null)
            throw new McpException($"Unknown project '{project}'. Use list_projects to discover valid slugs.");
        try
        {
            var result = await action();
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
            var value = (result as IValueHttpResult)?.Value;
            if (status >= 400)
                throw new McpException($"Operation failed (HTTP {status}): " +
                    (value is null ? "Resource not found or operation rejected." :
                        JsonSerializer.Serialize(value, McpTools.SerializerOptions)));
            return JsonSerializer.SerializeToElement(value ?? new { success = true, statusCode = status }, McpTools.SerializerOptions);
        }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
        catch (InvalidDataException ex) { throw new McpException(ex.Message); }
        catch (TimeoutException ex) { throw new McpException(ex.Message); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new McpException("The operation conflicts with existing records or immutable history."); }
    }
}
