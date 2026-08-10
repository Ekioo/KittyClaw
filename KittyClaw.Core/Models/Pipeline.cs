namespace KittyClaw.Core.Models;

/// <summary>A project-local workflow containing its own ordered board columns.</summary>
public class Pipeline
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Semantic role of a column. Parents use terminal roles to evaluate blocking children
/// without relying on display names such as "Done" or "Published".
/// </summary>
public enum ColumnRole
{
    // Values 0-3 are persisted in existing project databases. Never reorder them.
    Normal = 0,
    Waiting = 1,
    Success = 2,
    Failure = 3,
    /// <summary>A human decision or intervention is required before the ticket can continue.</summary>
    OwnerAction = 4,
    /// <summary>Work that cannot progress without an external dependency or intervention.</summary>
    Blocked = 5,
}
