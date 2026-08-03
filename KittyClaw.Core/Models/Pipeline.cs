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
    Normal,
    Waiting,
    /// <summary>A human decision or intervention is required before the ticket can continue.</summary>
    OwnerAction,
    Success,
    Failure,
}
