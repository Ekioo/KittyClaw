namespace Todo.Core.Models;

public class ActivityEntry
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public string Author { get; set; } = "owner";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public Ticket Ticket { get; set; } = null!;
}
