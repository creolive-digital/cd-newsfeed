namespace cd_newsfeed.Models;

public class NotificationPreference
{
    public int UserId { get; set; }
    public bool LikesEnabled { get; set; } = true;
    public bool CommentsEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
