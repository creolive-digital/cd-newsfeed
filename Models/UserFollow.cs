namespace cd_newsfeed.Models;

public class UserFollow
{
    public int Id { get; set; }
    public int FollowerUserId { get; set; }
    public int? FollowedUserId { get; set; }
    public string? Topic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
