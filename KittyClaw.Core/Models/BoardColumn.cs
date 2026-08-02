using System.Text.Json.Serialization;

namespace KittyClaw.Core.Models;

public class BoardColumn
{
    public int Id { get; set; }
    public int PipelineId { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#5a6a80";
    public int SortOrder { get; set; }
    public ColumnRole Role { get; set; } = ColumnRole.Normal;
}
