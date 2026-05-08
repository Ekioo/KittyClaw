namespace KittyClaw.Core.Models;

public class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? WorkspacePath { get; set; }
    public bool IsPaused { get; set; } = false;
    public string? FallbackModel { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
