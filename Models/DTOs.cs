namespace cd_newsfeed.Models;

public record RegisterRequest(string? DisplayName, string? Username, string? Password);

public record LoginRequest(string? Username, string? Password);

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
