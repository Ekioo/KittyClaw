using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ProgramActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_process_preserves_arguments_environment_and_exit_status(bool python)
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Program tests");
        var workspace = projects.ResolveWorkspacePath(project);
        var script = "import os,sys\nassert sys.argv[1] == 'space ; & argument'\nassert os.environ['CUSTOM'] == 'value'\nassert os.environ['KITTYCLAW_TICKET_TITLE'] == 'untrusted title'\nsys.exit(7)";
        var path = Path.Combine(workspace, "task.py");
        await File.WriteAllTextAsync(path, script);
        ExecuteProgramActionSpec spec = python
            ? new ExecutePythonActionSpec { Program = OperatingSystem.IsWindows() ? "python" : "python3", ScriptFile = "task.py" }
            : new ExecuteProgramActionSpec { Program = OperatingSystem.IsWindows() ? "python" : "python3", Arguments = [path] };
        spec.Arguments.Add("space ; & argument");
        spec.Env["CUSTOM"] = "value";
        var roundTrip = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec));
        Assert.Equal(spec.GetType(), roundTrip!.GetType());
        var executor = new ColumnActionExecutor(projects, new TicketService(projects, new MemberService(projects)),
            NullLogger<ColumnActionExecutor>.Instance);
        var result = await executor.ExecuteAsync(project.Slug, new ColumnProcessor { Name = "Test" },
            new ColumnExecution { Id = "test" }, new Ticket { Title = "untrusted title" },
            new ColumnProcessorAction("run", roundTrip), null, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("code 7", result.Error);
    }

    [Fact]
    public async Task Python_timeout_and_cancellation_stop_execution()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Timeout tests");
        var executor = new ColumnActionExecutor(projects, new TicketService(projects, new MemberService(projects)),
            NullLogger<ColumnActionExecutor>.Instance);
        var spec = new ExecutePythonActionSpec { Script = "import time; time.sleep(60)", TimeoutSeconds = 1 };
        Task<ColumnActionResult> Execute(CancellationToken token) => executor.ExecuteAsync(project.Slug,
            new ColumnProcessor { Name = "Timeout" }, new ColumnExecution { Id = "timeout" },
            new Ticket { Title = "" }, new ColumnProcessorAction("wait", spec), null, token);
        var result = await Execute(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("exceeded", result.Error);
        spec.TimeoutSeconds = 60;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(cancellation.Token));
    }

    [Fact]
    public void Python_prefers_project_virtual_environment_and_keeps_module_arguments_separate()
    {
        using var temp = new TempDir();
        var pythonPath = Path.Combine(temp.Path, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
        File.WriteAllText(pythonPath, "");
        var start = ProgramActionSupport.BuildStartInfo(new ExecutePythonActionSpec { Module = "package.worker" }, temp.Path);
        Assert.Equal(Path.GetFullPath(pythonPath), start.FileName);
        Assert.Equal(new[] { "-u", "-m", "package.worker" }, start.ArgumentList);
        Assert.Throws<InvalidOperationException>(() => ProgramActionSupport.Validate(new ExecuteProgramActionSpec
        { Program = "git", WorkingDirectory = "../outside" }));
        Assert.Throws<InvalidOperationException>(() => ProgramActionSupport.Validate(new ExecutePythonActionSpec
        { ScriptFile = "task.py", Module = "worker" }));
    }
}
