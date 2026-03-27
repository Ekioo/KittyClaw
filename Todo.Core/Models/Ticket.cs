namespace Todo.Core.Models;

public class Ticket
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.Backlog;
    public string CreatedBy { get; set; } = "owner";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Comment> Comments { get; set; } = [];
    public List<ActivityEntry> Activities { get; set; } = [];
    public List<Label> Labels { get; set; } = [];
}
