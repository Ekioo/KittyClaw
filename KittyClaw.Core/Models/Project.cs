namespace KittyClaw.Core.Models;

using System.ComponentModel.DataAnnotations.Schema;
using KittyClaw.Core.Automation;

public class Project
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? WorkspacePath { get; set; }
    public string? RepositoryPath { get; set; }
    [NotMapped] public string? ResolvedRepositoryPath { get; set; }
    public bool IsPaused { get; set; } = false;
    public string? FallbackModel { get; set; }
    public string? LocalModelBaseUrl { get; set; }
    public string? LocalModelName { get; set; }
    public bool RtkEnabled { get; set; }
    public BoundaryEnforcementMode BoundaryEnforcement { get; set; } = BoundaryEnforcementMode.Observe;
    public bool WorktreesEnabled { get; set; }
    public string? IntegrationBranch { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
