using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnActionExecutorTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly ColumnActionExecutor _executor;

    public ColumnActionExecutorTests()
    {
        _projects = new ProjectService(_temp.Path);
        _tickets = new TicketService(_projects, new MemberService(_projects));
        _executor = new ColumnActionExecutor(
            _projects, _tickets, NullLogger<ColumnActionExecutor>.Instance);
    }

    [Fact]
    public async Task PowerShell_action_receives_stable_execution_key_and_ticket_placeholders()
    {
        var project = await _projects.CreateProjectAsync("Script action");
        var column = (await new ColumnService(_projects).ListColumnsAsync(project.Slug))[0];
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Run script", status: column.Name,
            pipelineId: column.PipelineId, columnId: column.Id);
        var processor = new ColumnProcessor { Id = 1, ColumnId = column.Id, Name = "Worker" };
        var execution = new ColumnExecution
        {
            Id = "run1", ProcessorId = 1, TicketId = ticket.Id, Status = ColumnExecutionStatus.Running,
        };
        var action = new ColumnProcessorAction("script", new ExecutePowerShellActionSpec
        {
            Script = $"if ($env:KITTYCLAW_ACTION_EXECUTION_ID -ne 'run1:script') {{ exit 9 }}; " +
                     $"if ('{{ticketId}}' -ne '{ticket.Id}') {{ exit 8 }}",
        });

        var result = await _executor.ExecuteAsync(
            project.Slug, processor, execution, ticket, action, null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task Non_zero_script_and_invalid_http_are_reported_as_action_failures()
    {
        var project = await _projects.CreateProjectAsync("Failed actions");
        var column = (await new ColumnService(_projects).ListColumnsAsync(project.Slug))[0];
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Fail", status: column.Name,
            pipelineId: column.PipelineId, columnId: column.Id);
        var processor = new ColumnProcessor { Id = 1, ColumnId = column.Id, Name = "Worker" };
        var execution = new ColumnExecution
        {
            Id = "run2", ProcessorId = 1, TicketId = ticket.Id, Status = ColumnExecutionStatus.Running,
        };

        var script = await _executor.ExecuteAsync(project.Slug, processor, execution, ticket,
            new("script", new ExecutePowerShellActionSpec { Script = "exit 7" }), null, CancellationToken.None);
        var http = await _executor.ExecuteAsync(project.Slug, processor, execution, ticket,
            new("http", new HttpRequestActionSpec { Url = "file:///secret" }), null, CancellationToken.None);

        Assert.False(script.Succeeded);
        Assert.Contains("code 7", script.Error);
        Assert.False(http.Succeeded);
        Assert.Contains("HTTP(S)", http.Error);
    }

    public void Dispose() => _temp.Dispose();
}
