namespace cd_newsfeed.Models;

public class NewsSource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FeedUrl { get; set; } = string.Empty;
    public string Tag { get; set; } = "#Tech";
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedAt { get; set; }
    public int? LastItemCount { get; set; }
    public string? LastError { get; set; }
}
