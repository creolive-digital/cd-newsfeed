namespace cd_newsfeed.Models;

public record RegisterRequest(string? DisplayName, string? Username, string? Password, string? Email = null);

public record LoginRequest(string? Username, string? Password);

public record UpdateProfileRequest(string? DisplayName, string? Bio, string? Location, string? WebsiteUrl, string? Email = null);

public record PasswordResetRequest(string? Email);
public record PasswordResetConfirmRequest(string? Token, string? NewPassword);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public record UpdateNotificationPreferencesRequest(bool LikesEnabled, bool CommentsEnabled);
public record UpdatePostRequest(string? SinhalaTitle, string? SinhalaSummary, string? SinhalaContent, string? Tag, string? Link, string? ImageUrl);
public record CreateReportRequest(int? PostId, int? CommentId, string? Reason);
public record NewsSourceRequest(string? Name, string? FeedUrl, string? Tag, bool? IsEnabled = null);
public record TestNewsSourceRequest(string? FeedUrl);
public record SetNewsSourceEnabledRequest(bool IsEnabled);
public record SetNewsVisibilityRequest(bool Visible);

public record CreatePostRequest(
    string? SinhalaTitle,
    string? SinhalaSummary,
    string? Tag,
    string? Link,
    string? ImageUrl = null,
    string? SinhalaContent = null
);

public record CreateCommentRequest(string? Content);

public record UserDto(int Id, string DisplayName, string Username, string Role, bool IsActive, DateTime CreatedAt);

public record PostDto(
    int Id,
    string SinhalaTitle,
    string SinhalaSummary,
    string? SinhalaContent,
    string Source,
    string? Link,
    string? ImageUrl,
    string Tag,
    bool IsAutoNews,
    string Status,
    DateTime CreatedAt,
    AuthorDto? Author,
    List<CommentDto> Comments
);

public record AuthorDto(int Id, string DisplayName, string Username);

public record CommentDto(int Id, int PostId, string Content, DateTime CreatedAt, AuthorDto User);
