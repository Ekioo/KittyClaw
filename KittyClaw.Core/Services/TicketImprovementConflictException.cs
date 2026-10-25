namespace KittyClaw.Core.Services;

public sealed class TicketImprovementConflictException(string message) : InvalidOperationException(message);
