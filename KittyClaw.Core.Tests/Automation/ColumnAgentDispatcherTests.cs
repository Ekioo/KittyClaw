using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnAgentDispatcherTests
{
    [Fact]
    public void Result_parser_preserves_criterion_assessments_and_their_proof()
    {
        var parsed = ColumnAgentDispatcher.TryParseResult("""
            {"outcome":"completed","skillsUsed":[],"acceptanceCriteria":[{"id":"criterion-1","status":"PARTIAL","evidence":{"content":"One check remains","mediaType":"text/plain"}}]}
            """, out var result, out var error);
        Assert.True(parsed, error);
        var assessment = Assert.Single(result!.AcceptanceCriteria!);
        Assert.Equal("criterion-1", assessment.Id);
        Assert.Equal(AcceptanceStatus.NeedsRework, assessment.Status);
        Assert.Equal("One check remains", assessment.Evidence!.Content);
    }

    [Fact]
    public void Approved_JEV_profile_prevents_silent_escalation_to_the_project_fallback()
    {
        var project = new Project { Name = "Profiles", Slug = "profiles", FallbackModel = "claude-sonnet-4-6" };
        var execution = new ColumnExecution { Id = "approved" };
        Assert.NotNull(ColumnAgentDispatcher.ResolveFallback(project, execution));
        execution.JevExecutionProfileJson = "{}";
        Assert.Null(ColumnAgentDispatcher.ResolveFallback(project, execution));
    }

    [Fact]
    public void Runtime_contract_reserves_ticket_routing_and_uses_canonical_memory_path()
    {
        var processor = new ColumnProcessor
        {
            Id = 4,
            ColumnId = 19,
            Name = "Video producer",
        };

        var contract = ColumnAgentDispatcher.BuildRuntimeContract(processor);

        Assert.Contains("Never PATCH or otherwise change the current ticket's pipeline, column, status, or assignedTo", contract);
        Assert.Contains("final JSON outcome", contract);
        Assert.Contains(".agents/processors/column-19/memory/MEMORY.md", contract);
        Assert.Contains("never create or use `.agents/column-19/memory.md`", contract);
    }

    [Fact]
    public void Result_parser_accepts_scheduled_wake_contract()
    {
        const string json = """
            {"outcome":"scheduled","skillsUsed":[],"summary":"Later.","fireAt":"2026-08-08T08:00:00Z","scheduleTarget":"À traiter"}
            """;

        var parsed = ColumnAgentDispatcher.TryParseResult(json, out var result, out var error);

        Assert.True(parsed, error);
        Assert.Equal(new DateTime(2026, 8, 8, 8, 0, 0, DateTimeKind.Utc), result!.FireAt);
        Assert.Equal("À traiter", result.ScheduleTarget);
    }

    [Fact]
    public void Result_parser_preserves_explicit_reusable_lessons()
    {
        const string json = """
            {"outcome":"completed","skillsUsed":[],"summary":"Done.","lessons":["Use a stable checkpoint before routing."]}
            """;

        var parsed = ColumnAgentDispatcher.TryParseResult(json, out var result, out var error);

        Assert.True(parsed, error);
        Assert.Equal(["Use a stable checkpoint before routing."], result!.Lessons);
    }
}
