namespace cd_newsfeed.Models;

public class Post
{
    public int Id { get; set; }
    public string SinhalaTitle { get; set; } = string.Empty;
    public string SinhalaSummary { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // e.g. "TechCrunch AI" or user's DisplayName
    public string? Link { get; set; } // nullable for user posts
    public string? ImageUrl { get; set; } // article or preview image URL
    public string? SinhalaContent { get; set; } // full article body in Sinhala
    public string Tag { get; set; } = "#Community"; // #AI, #Tech, or #Community
    public bool IsAutoNews { get; set; }
    public string Status { get; set; } = "Pending"; // "Approved" for auto-news, "Pending" for user-submitted posts
    public int? AuthorId { get; set; }
    public User? Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
