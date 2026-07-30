namespace KittyClaw.Core.Services;

/// <summary>
/// Optimistic-concurrency failure on a ticket update: the caller asked for the change to
/// apply only while the ticket was still in <see cref="ExpectedStatus"/>, but it has moved
/// on (<see cref="ActualStatus"/>) — typically another agent grabbed the ticket first.
/// Mapped to HTTP 409 by the API so the loser is told explicitly instead of silently
/// overwriting the winner.
/// </summary>
public sealed class TicketTransitionConflictException(string actualStatus, string expectedStatus)
    : InvalidOperationException($"Le ticket est en '{actualStatus}', pas en '{expectedStatus}' — mise à jour refusée (conflit de concurrence).")
{
    public string ActualStatus { get; } = actualStatus;
    public string ExpectedStatus { get; } = expectedStatus;
}
