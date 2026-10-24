namespace KittyClaw.Core.Automation;

/// <summary>Project-wide launch budget stored in .agents/agent-budget.json.</summary>
public sealed record AgentBudgetSettings(decimal? DailyBudgetUsd = null);
