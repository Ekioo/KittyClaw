using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Models;

/// <summary>Persistent generic agent profile attached to one active workflow column.</summary>
public class ColumnProcessor
{
    public int Id { get; set; }
    public int ColumnId { get; set; }
    public required string Name { get; set; }
    public string Mission { get; set; } = "Process the selected ticket and return a structured outcome.";
    public string? Model { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxTurns { get; set; } = 100;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string AvailableSkillsJson { get; set; } = "[]";
    [JsonIgnore]
    public string RecommendedSkillsJson { get; set; } = "[]";
    [JsonIgnore]
    public string RequiredSkillsJson { get; set; } = "[]";

    [NotMapped]
    public List<string> AvailableSkills
    {
        get => Deserialize(AvailableSkillsJson);
        set => AvailableSkillsJson = Serialize(value);
    }

    [NotMapped]
    public List<string> RecommendedSkills
    {
        get => Deserialize(RecommendedSkillsJson);
        set => RecommendedSkillsJson = Serialize(value);
    }

    [NotMapped]
    public List<string> RequiredSkills
    {
        get => Deserialize(RequiredSkillsJson);
        set => RequiredSkillsJson = Serialize(value);
    }

    private static List<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string Serialize(IEnumerable<string> values) => JsonSerializer.Serialize(
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
}

public sealed record ProjectSkill(string Slug, string Name, string InstructionsPath);
