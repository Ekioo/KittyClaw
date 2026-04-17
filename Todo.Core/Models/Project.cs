namespace Todo.Core.Models;

public class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? WorkspacePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
