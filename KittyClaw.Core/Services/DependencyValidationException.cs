namespace KittyClaw.Core.Services;

/// <summary>
/// Thrown by <see cref="TicketService.AddDependencyAsync"/> when a dependency edge fails
/// structural or state validation. <see cref="Reason"/> is a machine-readable code.
/// </summary>
public sealed class DependencyValidationException(string reason, string message) : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
}
