namespace KittyClaw.Core.Services;

public static class DashboardRunPolicy
{
    // Dashboard prompts often need to collect data, inspect the result, then format it.
    // Five turns proved too tight across multiple providers while ten remains bounded.
    public const int MaxTurns = 10;
}
