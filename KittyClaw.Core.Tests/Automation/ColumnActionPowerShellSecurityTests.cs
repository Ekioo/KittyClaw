using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnActionPowerShellSecurityTests : IDisposable
{
    private readonly TempDir _temp = new();

    public static TheoryData<string, string> LiteralArguments => new()
    {
        { "{ticketTitle}", "-" },
        { "{ticketTitle}", " -Publish" },
        { "{ticketTitle}", "ordinary\" -Publish" },
        { "{summary}", "ordinary\" -Publish" },
        { "{ticketTitle}", "a\"b" },
        { "{summary}", "a\"b" },
        { "{ticketTitle}", @"C:\path with spaces\" },
        { "{summary}", @"C:\path with spaces\" },
        { "{ticketTitle}", "O'Brien $(exit 71); & <tag> \u00e9" },
        { "{summary}", "O'Brien $(exit 71); & <tag> \u00e9" },
    };

    [Theory]
    [MemberData(nameof(LiteralArguments))]
    public async Task Ticket_and_model_text_remain_one_literal_script_argument(string placeholder, string value)
    {
        var script = Path.Combine(_temp.Path, "script with spaces.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$Text, [switch]$Publish)
            if ($Publish) { exit 71 }
            if ([string]$Text -cne $env:KITTYCLAW_AUDIT_EXPECTED) { exit 72 }
            if ($args.Count -ne 0) { exit 73 }
            """);
        var (executor, slug) = await CreateExecutorAsync();
        var spec = new ExecutePowerShellActionSpec
        {
            ScriptFile = script,
            Arguments = [placeholder],
            Env = new() { ["KITTYCLAW_AUDIT_EXPECTED"] = value },
        };
        var result = await ExecuteAsync(executor, slug, spec, value);
        Assert.True(result.Succeeded, result.Error);
    }

    [Theory]
    [InlineData("{ticketTitle}")]
    [InlineData("{ticketStatus}")]
    [InlineData("{assignee}")]
    [InlineData("{outcome}")]
    [InlineData("{summary}")]
    public async Task Untrusted_text_cannot_be_interpolated_into_inline_script_source(string placeholder)
    {
        var marker = Path.Combine(_temp.Path, "source-was-evaluated.txt");
        var (executor, slug) = await CreateExecutorAsync();
        var spec = new ExecutePowerShellActionSpec
        {
            Script = "[Console]::WriteLine(\"before " + placeholder + " after\"); exit 0",
            Env = new() { ["KITTYCLAW_AUDIT_MARKER"] = marker },
        };
        const string value = "$( [IO.File]::WriteAllText($env:KITTYCLAW_AUDIT_MARKER, 'evaluated'))";
        var result = await ExecuteAsync(executor, slug, spec, value);
        Assert.False(File.Exists(marker), "Ticket/model text was evaluated as PowerShell code.");
        Assert.False(result.Succeeded);
        Assert.Contains("environment", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inline_script_reads_untrusted_text_from_environment_without_evaluating_it()
    {
        var marker = Path.Combine(_temp.Path, "environment-was-evaluated.txt");
        var (executor, slug) = await CreateExecutorAsync();
        var spec = new ExecutePowerShellActionSpec
        {
            Script = """
                foreach ($name in @('KITTYCLAW_TICKET_TITLE', 'KITTYCLAW_TICKET_STATUS',
                    'KITTYCLAW_TICKET_ASSIGNEE', 'KITTYCLAW_ACTION_OUTCOME', 'KITTYCLAW_ACTION_SUMMARY')) {
                    if ([Environment]::GetEnvironmentVariable($name) -cne $env:KITTYCLAW_AUDIT_EXPECTED) { exit 74 }
                }
                if ('{ticketId}' -ne '17') { exit 75 }
                """,
        };
        const string value = "$( [IO.File]::WriteAllText($env:KITTYCLAW_AUDIT_MARKER, 'evaluated')) \" ' ` ; &\n";
        spec.Env["KITTYCLAW_AUDIT_EXPECTED"] = value;
        spec.Env["KITTYCLAW_AUDIT_MARKER"] = marker;
        // Caller-supplied environment must not replace the actual ticket/model values.
        spec.Env["KITTYCLAW_TICKET_TITLE"] = "forged";
        var result = await ExecuteAsync(executor, slug, spec, value);
        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("{ticketTitle}", "-")]
    [InlineData("{summary}", "-")]
    [InlineData("{ticketTitle}", "\u2013")]
    [InlineData("{summary}", "\u2013")]
    [InlineData("{ticketTitle}", "\u2014")]
    [InlineData("{summary}", "\u2014")]
    [InlineData("{ticketTitle}", "\u2015")]
    [InlineData("{summary}", "\u2015")]
    public async Task Dynamic_values_cannot_select_script_parameters(string placeholder, string prefix)
    {
        var marker = Path.Combine(_temp.Path, "option-was-enabled.txt");
        var script = Path.Combine(_temp.Path, "script.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$Text, [switch]$Publish)
            if ($Publish) { [IO.File]::WriteAllText($env:KITTYCLAW_AUDIT_MARKER, 'enabled') }
            """);
        var (executor, slug) = await CreateExecutorAsync();
        var result = await ExecuteAsync(executor, slug, new ExecutePowerShellActionSpec
        {
            ScriptFile = script, Arguments = [placeholder],
            Env = new() { ["KITTYCLAW_AUDIT_MARKER"] = marker },
        }, prefix + "Publish");
        Assert.False(File.Exists(marker), "Untrusted text selected a script switch.");
        Assert.False(result.Succeeded);
        Assert.Contains("environment", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configured_script_switches_remain_available()
    {
        var script = Path.Combine(_temp.Path, "script.ps1");
        await File.WriteAllTextAsync(script, "param([switch]$Publish)\nif (-not $Publish) { exit 76 }");
        var (executor, slug) = await CreateExecutorAsync();
        var result = await ExecuteAsync(executor, slug,
            new ExecutePowerShellActionSpec { ScriptFile = script, Arguments = ["-Publish"] }, "unused");
        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task Windows_PowerShell_fallback_also_preserves_literal_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = Environment.GetEnvironmentVariable("PATH");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0");
        Assert.True(File.Exists(Path.Combine(directory, "powershell.exe")));
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory);
            await Ticket_and_model_text_remain_one_literal_script_argument("{ticketTitle}", "ordinary\" -Publish");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    private async Task<(ColumnActionExecutor Executor, string Slug)> CreateExecutorAsync()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("PowerShell argument audit");
        return (new ColumnActionExecutor(projects, new TicketService(projects, new MemberService(projects)),
            NullLogger<ColumnActionExecutor>.Instance), project.Slug);
    }

    private static Task<ColumnActionResult> ExecuteAsync(ColumnActionExecutor executor, string slug,
        ExecutePowerShellActionSpec spec, string value) => executor.ExecuteAsync(slug,
        new ColumnProcessor { Id = 1, ColumnId = 1, Name = "Fixture" },
        new ColumnExecution { Id = "fixture", ProcessorId = 1, TicketId = 17 },
        new Ticket { Id = 17, Title = value, Status = value, AssignedTo = value },
        new ColumnProcessorAction("script", spec), new ColumnAgentResult(value, [], value), CancellationToken.None);

    public void Dispose() => _temp.Dispose();
}
