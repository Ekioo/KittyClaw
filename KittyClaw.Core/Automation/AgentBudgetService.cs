using System.Text.Json;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed record AgentBudgetDecision(bool Allowed, decimal? LimitUsd, decimal SpentUsd, string? Reason);

/// <summary>Project-wide admission policy shared by every provider process launch.</summary>
public sealed class AgentBudgetService(ProjectService projects, CostTracker costs, AgentRunRegistry runs)
{
    public async Task<AgentBudgetDecision> CheckAsync(string projectSlug, CancellationToken ct = default)
    {
        try
        {
            var project = await projects.GetProjectAsync(projectSlug)
                ?? throw new InvalidOperationException("The project does not exist.");
            // Resolve the control workspace, never a caller's ticket or maintenance worktree.
            var workspace = projects.ResolveWorkspacePath(project);
            var path = Path.Combine(workspace, ".agents", "automations.json");
            AutomationConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<AutomationConfig>(await File.ReadAllTextAsync(path, ct), AutomationStore.JsonOptions)
                    ?? throw new InvalidDataException("The budget configuration is empty.");
            }
            catch (FileNotFoundException) { return new(true, null, 0m, null); }
            catch (DirectoryNotFoundException) { return new(true, null, 0m, null); }

            if (config.DailyBudgetUsd is not decimal limit || limit <= 0)
                return new(true, config.DailyBudgetUsd, 0m, null);

            var spent = costs.GetDailySpendUsd(workspace, runs.AllForProject(projectSlug));
            return spent < limit
                ? new(true, limit, spent, null)
                : new(false, limit, spent, FormattableString.Invariant(
                    $"Agent launch blocked: daily budget reached (${spent:0.######} spent / ${limit:0.######} limit, UTC day)."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return new(false, null, 0m,
                "Agent launch blocked: the daily budget could not be checked. Check the project budget configuration and cost journal.");
        }
    }
}
