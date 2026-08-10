namespace KittyClaw.Core.Services;

public sealed class TicketCreationSaturationException : InvalidOperationException
{
    public const string ErrorCode = "blocked_ticket_limit_reached";

    public TicketCreationSaturationException(
        string projectSlug, int blockedCount, int blockedLimit, IReadOnlyList<int> blockedColumnIds)
        : base($"Project '{projectSlug}' has {blockedCount} blocked tickets; the configured limit is {blockedLimit}.")
    {
        ProjectSlug = projectSlug;
        BlockedCount = blockedCount;
        BlockedLimit = blockedLimit;
        BlockedColumnIds = blockedColumnIds;
    }

    public string ProjectSlug { get; }
    public int BlockedCount { get; }
    public int BlockedLimit { get; }
    public IReadOnlyList<int> BlockedColumnIds { get; }
}
