namespace cd_newsfeed.Models;

public class Notification
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public int ActorUserId { get; set; }
    public int? PostId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Preview { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
