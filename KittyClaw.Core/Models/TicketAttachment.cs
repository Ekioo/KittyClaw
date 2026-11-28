namespace KittyClaw.Core.Models;

public sealed class TicketAttachment
{
    public string Id { get; set; } = "";
    public int TicketId { get; set; }
    public required string FileName { get; set; }
    public long Size { get; set; }
    public required string Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Sha256 { get; set; } = "";
    public string? IdempotencyKeyHash { get; set; }
    public int? CommentId { get; set; }
    public DateTime? DeletedAt { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Replayed { get; set; }
}
