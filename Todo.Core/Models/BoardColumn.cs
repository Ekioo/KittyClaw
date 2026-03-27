using System.Text.Json.Serialization;

namespace Todo.Core.Models;

public class BoardColumn
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#5a6a80";
    public int SortOrder { get; set; }
}
