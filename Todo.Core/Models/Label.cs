namespace Todo.Core.Models;

public class Label
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#6366f1"; // hex color

    [System.Text.Json.Serialization.JsonIgnore]
    public List<Ticket> Tickets { get; set; } = [];
}
