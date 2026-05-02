namespace KittyClaw.Core.Models;

public class ChatMessageRow
{
    public int Id { get; set; }
    public required string TargetSlug { get; set; }
    public required string Role { get; set; }
    public required string Text { get; set; }
    public string? ToolName { get; set; }
    public string? Detail { get; set; }
    public required string CreatedAt { get; set; }
}
