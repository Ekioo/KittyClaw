using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Models;

public sealed record AskTicketQuestionRequest(string Question, List<string> Options, bool MultiSelect = false,
    bool AllowFreeText = true, string Author = "agent", string? ExecutionId = null);
public sealed record AnswerTicketQuestionRequest(List<string> SelectedOptions, string? FreeText = null);

public sealed class TicketQuestion
{
    public required string Id { get; set; }
    public int TicketId { get; set; }
    public required string ExecutionId { get; set; }
    public required string Question { get; set; }
    public string Author { get; set; } = "agent";
    public bool MultiSelect { get; set; }
    public bool AllowFreeText { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAt { get; set; }
    public string? FreeText { get; set; }
    public bool Cancelled { get; set; }
    [JsonIgnore] public string OptionsJson { get; set; } = "[]";
    [JsonIgnore] public string SelectedOptionsJson { get; set; } = "[]";
    [NotMapped] public List<string> Options { get => JsonSerializer.Deserialize<List<string>>(OptionsJson)!; set => OptionsJson = JsonSerializer.Serialize(value); }
    [NotMapped] public List<string> SelectedOptions { get => JsonSerializer.Deserialize<List<string>>(SelectedOptionsJson)!; set => SelectedOptionsJson = JsonSerializer.Serialize(value); }
}
