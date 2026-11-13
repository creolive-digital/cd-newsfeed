using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using cd_newsfeed.Data;
using cd_newsfeed.Models;
using cd_newsfeed.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure a test-only in-memory database or the normal MSSQL database.
var useInMemoryDb = bool.TryParse(builder.Configuration["USE_INMEMORY_DB"], out var inMemoryDbEnabled)
    && inMemoryDbEnabled;
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=(localdb)\\MSSQLLocalDB;Database=CdNewsfeedDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true;";
var inMemoryDatabaseName = builder.Configuration["INMEMORY_DB_NAME"] ?? "CdNewsfeed";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (useInMemoryDb)
    {
        options.UseInMemoryDatabase(inMemoryDatabaseName);
    }
    else
    {
        options.UseSqlServer(connectionString);
    }
});

// 2. HTTP Clients & Background Services
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("RssClient", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 (compatible; CDNewsfeed/1.0)");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<GeminiTranslatorService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHostedService<NewsUpdaterService>();

// 3. Password Hasher
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// 4. Cookie Authentication & Authorization
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "CDNewsfeed.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // Ensure API responses return 401/403 instead of 302 redirects
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// 5. CORS Configuration (supporting frontend with credentials)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Initialize the configured database. Fail startup if persistence is unavailable so
// the app never appears healthy while writes are being lost.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (!useInMemoryDb)
    {
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Posts') AND name = 'ImageUrl')
                ALTER TABLE [Posts] ADD [ImageUrl] nvarchar(1000) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Posts') AND name = 'SinhalaContent')
                ALTER TABLE [Posts] ADD [SinhalaContent] nvarchar(max) NULL;
        ");
    }

    // Hide legacy canned seed articles. Keep any associated user discussions intact.
    var legacySeedPosts = await db.Posts
        .Where(p => p.IsAutoNews && (p.Link == "https://techcrunch.com/category/artificial-intelligence/" || p.Link == "https://www.theverge.com/tech"))
        .ToListAsync();
    foreach (var post in legacySeedPosts)
    {
        post.Status = "Archived";
    }
    if (legacySeedPosts.Count > 0)
    {
        await db.SaveChangesAsync();
    }
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";
        if (path.EndsWith(".html") || path == "/" || path.EndsWith("sw.js") || path.EndsWith("manifest.json"))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Keep administrator sign-in off the public homepage; serve it only at /admin.
app.MapGet("/admin", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "admin", "index.html"), "text/html"));

// ==========================================
// Authentication & User Endpoints
// ==========================================

// POST /api/auth/register
app.MapPost("/api/auth/register", async (RegisterRequest req, AppDbContext db, IPasswordHasher<User> passwordHasher, HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(req.DisplayName) ||
        string.IsNullOrWhiteSpace(req.Username) ||
        string.IsNullOrWhiteSpace(req.Password))
    {
        return Results.BadRequest(new { message = "DisplayName, Username, and Password are required." });
    }

    if (req.DisplayName.Trim().Length > 100 || req.Username.Trim().Length > 50 || req.Password.Length < 8)
    {
        return Results.BadRequest(new { message = "Display name must be 100 characters or fewer, username 50 characters or fewer, and password at least 8 characters." });
    }

    var cleanUsername = req.Username.Trim();
    var usernameLower = cleanUsername.ToLower();

    var adminUsername = app.Configuration["AdminAuth:Username"]?.Trim();
    var adminPassword = app.Configuration["AdminAuth:Password"];
    if ((!string.IsNullOrWhiteSpace(adminUsername) && string.Equals(cleanUsername, adminUsername, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(adminPassword) && FixedTimeEquals(adminPassword, req.Password)))
    {
        return Results.BadRequest(new { message = "Choose credentials different from the administrator sign-in." });
    }

    var exists = await db.Users.AnyAsync(u => u.Username.ToLower() == usernameLower);
    if (exists)
    {
        return Results.Conflict(new { message = "Username is already taken." });
    }

    var user = new User
    {
        DisplayName = req.DisplayName.Trim(),
        Username = cleanUsername,
        Role = "User",
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    user.PasswordHash = passwordHasher.HashPassword(user, req.Password);
    db.Users.Add(user);
    await db.SaveChangesAsync();

    // Sign the user in
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new("DisplayName", user.DisplayName),
        new(ClaimTypes.Role, user.Role)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
    });

    return Results.Created($"/api/users/{user.Id}", new
    {
        id = user.Id,
        displayName = user.DisplayName,
        username = user.Username,
        role = user.Role
    });
});

// POST /api/admin/auth/login: Dedicated administrator credentials, separate from user accounts.
app.MapPost("/api/admin/auth/login", async (LoginRequest req, AppDbContext db, IPasswordHasher<User> passwordHasher, HttpContext httpContext) =>
{
    var configuredUsername = app.Configuration["AdminAuth:Username"]?.Trim();
    var configuredPassword = app.Configuration["AdminAuth:Password"];
    if (string.IsNullOrWhiteSpace(configuredUsername) || string.IsNullOrEmpty(configuredPassword))
    {
        return Results.Json(new { message = "Administrator sign-in is not configured on this server." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password) ||
        !FixedTimeEquals(configuredUsername, req.Username.Trim()) || !FixedTimeEquals(configuredPassword, req.Password))
    {
        return Results.Json(new { message = "Invalid administrator username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (await db.Users.AnyAsync(u => u.Role != "Admin" && u.Username.ToLower() == configuredUsername.ToLower()))
    {
        return Results.Conflict(new { message = "The configured administrator username is already in use by a regular account." });
    }

    var adminUser = await db.Users.OrderBy(u => u.Id).FirstOrDefaultAsync(u => u.Role == "Admin");
    if (adminUser is { IsActive: false })
    {
        return Results.Json(new { message = "The administrator account is deactivated." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (adminUser == null)
    {
        if (await db.Users.AnyAsync(u => u.Username.ToLower() == configuredUsername.ToLower()))
        {
            return Results.Conflict(new { message = "The configured administrator username is already in use by a regular account." });
        }

        adminUser = new User
        {
            DisplayName = "Administrator",
            Username = configuredUsername,
            Role = "Admin",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        adminUser.PasswordHash = passwordHasher.HashPassword(adminUser, configuredPassword);
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, adminUser.Id.ToString()),
        new(ClaimTypes.Name, adminUser.Username),
        new("DisplayName", adminUser.DisplayName),
        new(ClaimTypes.Role, adminUser.Role)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
    });

    return Results.Ok(new
    {
        id = adminUser.Id,
        displayName = adminUser.DisplayName,
        username = adminUser.Username,
        role = adminUser.Role
    });
});

// POST /api/auth/login
app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, IPasswordHasher<User> passwordHasher, HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
    {
        return Results.BadRequest(new { message = "Username and Password are required." });
    }

    var usernameLower = req.Username.Trim().ToLower();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == usernameLower);

    var adminUsername = app.Configuration["AdminAuth:Username"]?.Trim();
    var adminPassword = app.Configuration["AdminAuth:Password"];
    if (user?.Role == "Admin" ||
        (!string.IsNullOrWhiteSpace(adminUsername) && string.Equals(req.Username.Trim(), adminUsername, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(adminPassword) && FixedTimeEquals(adminPassword, req.Password)))
    {
        return Results.Json(new { message = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (user == null)
    {
        return Results.Json(new { message = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var verifyResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
    if (verifyResult == PasswordVerificationResult.Failed)
    {
        return Results.Json(new { message = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // Block login if IsActive == false with a clear deactivated message
    if (!user.IsActive)
    {
        return Results.Json(new { message = "Your account has been deactivated. Please contact an administrator." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (verifyResult == PasswordVerificationResult.SuccessRehashNeeded)
    {
        user.PasswordHash = passwordHasher.HashPassword(user, req.Password);
        await db.SaveChangesAsync();
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new("DisplayName", user.DisplayName),
        new(ClaimTypes.Role, user.Role)
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
    });

    return Results.Ok(new
    {
        id = user.Id,
        displayName = user.DisplayName,
        username = user.Username,
        role = user.Role
    });
});

// POST /api/auth/logout
app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out successfully." });
});

// GET /api/auth/me
app.MapGet("/api/auth/me", async (HttpContext httpContext, AppDbContext db) =>
{
    if (httpContext.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (!int.TryParse(userIdClaim, out var userId))
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.FindAsync(userId);
    if (user == null || !user.IsActive)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        id = user.Id,
        displayName = user.DisplayName,
        username = user.Username,
        role = user.Role
    });
});

// GET /api/feed (Requires authenticated & active user)
app.MapGet("/api/feed", async (HttpContext httpContext, AppDbContext db) =>
{
    var currentUser = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (currentUser == null)
    {
        return httpContext.User.Identity?.IsAuthenticated == true
            ? Results.Json(new { message = "Your account has been deactivated. Please contact an administrator." }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Unauthorized();
    }

    var posts = await db.Posts
        .AsNoTracking()
        .Where(p => p.Status == "Approved")
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new
        {
            id = p.Id,
            sinhalaTitle = p.SinhalaTitle,
            sinhalaSummary = p.SinhalaSummary,
            sinhalaContent = p.SinhalaContent,
            imageUrl = p.ImageUrl,
            source = p.Source,
            link = p.Link,
            tag = p.Tag,
            isAutoNews = p.IsAutoNews,
            status = p.Status,
            createdAt = p.CreatedAt,
            author = p.Author == null ? null : new
            {
                id = p.Author.Id,
                displayName = p.Author.DisplayName,
                username = p.Author.Username
            },
            comments = p.Comments
                .OrderBy(c => c.CreatedAt)
                .Select(c => new
                {
                    id = c.Id,
                    postId = c.PostId,
                    content = c.Content,
                    createdAt = c.CreatedAt,
                    user = new
                    {
                        id = c.User.Id,
                        displayName = c.User.DisplayName,
                        username = c.User.Username
                    }
                }).ToList()
        })
        .ToListAsync();

    return Results.Ok(posts);
});

// POST /api/posts (Authenticated users can submit a new post)
app.MapPost("/api/posts", async (CreatePostRequest req, HttpContext httpContext, AppDbContext db) =>
{
    var currentUser = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (currentUser == null)
    {
        return httpContext.User.Identity?.IsAuthenticated == true
            ? Results.Json(new { message = "Your account has been deactivated. Please contact an administrator." }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(req.SinhalaTitle) || string.IsNullOrWhiteSpace(req.SinhalaSummary))
    {
        return Results.BadRequest(new { message = "SinhalaTitle and SinhalaSummary are required." });
    }

    var tag = NormalizeTag(req.Tag);

    var post = new Post
    {
        SinhalaTitle = req.SinhalaTitle.Trim(),
        SinhalaSummary = req.SinhalaSummary.Trim(),
        SinhalaContent = string.IsNullOrWhiteSpace(req.SinhalaContent) ? null : req.SinhalaContent.Trim(),
        ImageUrl = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl.Trim(),
        Tag = tag,
        Link = string.IsNullOrWhiteSpace(req.Link) ? null : req.Link.Trim(),
        Source = currentUser.DisplayName,
        IsAutoNews = false,
        Status = "Pending", // saved with Status = "Pending" until an Admin approves it
        AuthorId = currentUser.Id,
        CreatedAt = DateTime.UtcNow
    };

    db.Posts.Add(post);
    await db.SaveChangesAsync();

    return Results.Created($"/api/posts/{post.Id}", new
    {
        id = post.Id,
        sinhalaTitle = post.SinhalaTitle,
        sinhalaSummary = post.SinhalaSummary,
        sinhalaContent = post.SinhalaContent,
        imageUrl = post.ImageUrl,
        source = post.Source,
        link = post.Link,
        tag = post.Tag,
        isAutoNews = post.IsAutoNews,
        status = post.Status,
        createdAt = post.CreatedAt,
        author = new
        {
            id = currentUser.Id,
            displayName = currentUser.DisplayName,
            username = currentUser.Username
        }
    });
});

// POST /api/posts/{id}/comments (Authenticated users can comment on any approved post)
app.MapPost("/api/posts/{id:int}/comments", async (int id, CreateCommentRequest req, HttpContext httpContext, AppDbContext db) =>
{
    var currentUser = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (currentUser == null)
    {
        return httpContext.User.Identity?.IsAuthenticated == true
            ? Results.Json(new { message = "Your account has been deactivated. Please contact an administrator." }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(req.Content))
    {
        return Results.BadRequest(new { message = "Comment content cannot be empty." });
    }

    var content = req.Content.Trim();
    if (content.Length > 2000)
    {
        return Results.BadRequest(new { message = "Comment content cannot exceed 2,000 characters." });
    }

    var post = await db.Posts.FindAsync(id);
    if (post == null)
    {
        return Results.NotFound(new { message = "Post not found." });
    }

    if (post.Status != "Approved")
    {
        return Results.BadRequest(new { message = "Comments can only be added to approved posts." });
    }

    var comment = new Comment
    {
        PostId = id,
        UserId = currentUser.Id,
        Content = content,
        CreatedAt = DateTime.UtcNow
    };

    db.Comments.Add(comment);
    await db.SaveChangesAsync();

    return Results.Created($"/api/posts/{id}/comments/{comment.Id}", new
    {
        id = comment.Id,
        postId = comment.PostId,
        content = comment.Content,
        createdAt = comment.CreatedAt,
        user = new
        {
            id = currentUser.Id,
            displayName = currentUser.DisplayName,
            username = currentUser.Username
        }
    });
});

// ==========================================
// Admin Endpoints (Require Role == "Admin")
// ==========================================
var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

// GET /api/admin/users: List all users
admin.MapGet("/users", async (AppDbContext db) =>
{
    var users = await db.Users
        .AsNoTracking()
        .OrderBy(u => u.Id)
        .Select(u => new
        {
            id = u.Id,
            displayName = u.DisplayName,
            username = u.Username,
            role = u.Role,
            isActive = u.IsActive,
            createdAt = u.CreatedAt
        })
        .ToListAsync();

    return Results.Ok(users);
});

// PATCH /api/admin/users/{id}/toggle-active: Activate or deactivate a user
admin.MapPatch("/users/{id:int}/toggle-active", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var currentUserIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (int.TryParse(currentUserIdClaim, out var currentUserId) && currentUserId == id)
    {
        return Results.BadRequest(new { message = "You cannot toggle activation on your own account." });
    }

    var user = await db.Users.FindAsync(id);
    if (user == null)
    {
        return Results.NotFound(new { message = "User not found." });
    }

    user.IsActive = !user.IsActive;
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        id = user.Id,
        displayName = user.DisplayName,
        username = user.Username,
        role = user.Role,
        isActive = user.IsActive,
        createdAt = user.CreatedAt
    });
});

// GET /api/admin/posts/pending: List all Pending user posts
admin.MapGet("/posts/pending", async (AppDbContext db) =>
{
    var pendingPosts = await db.Posts
        .AsNoTracking()
        .Where(p => p.Status == "Pending")
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new
        {
            id = p.Id,
            sinhalaTitle = p.SinhalaTitle,
            sinhalaSummary = p.SinhalaSummary,
            sinhalaContent = p.SinhalaContent,
            imageUrl = p.ImageUrl,
            source = p.Source,
            link = p.Link,
            tag = p.Tag,
            isAutoNews = p.IsAutoNews,
            status = p.Status,
            createdAt = p.CreatedAt,
            author = p.Author == null ? null : new
            {
                id = p.Author.Id,
                displayName = p.Author.DisplayName,
                username = p.Author.Username
            }
        })
        .ToListAsync();

    return Results.Ok(pendingPosts);
});

// PATCH /api/admin/posts/{id}/approve: Approve a pending post
admin.MapPatch("/posts/{id:int}/approve", async (int id, AppDbContext db) =>
{
    var post = await db.Posts.Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == id);
    if (post == null)
    {
        return Results.NotFound(new { message = "Post not found." });
    }

    post.Status = "Approved";
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        id = post.Id,
        sinhalaTitle = post.SinhalaTitle,
        sinhalaSummary = post.SinhalaSummary,
        sinhalaContent = post.SinhalaContent,
        imageUrl = post.ImageUrl,
        source = post.Source,
        link = post.Link,
        tag = post.Tag,
        isAutoNews = post.IsAutoNews,
        status = post.Status,
        createdAt = post.CreatedAt,
        author = post.Author == null ? null : new
        {
            id = post.Author.Id,
            displayName = post.Author.DisplayName,
            username = post.Author.Username
        }
    });
});

// DELETE /api/admin/posts/{id}: Delete/reject any post
admin.MapDelete("/posts/{id:int}", async (int id, AppDbContext db) =>
{
    var post = await db.Posts.FindAsync(id);
    if (post == null)
    {
        return Results.NotFound(new { message = "Post not found." });
    }

    db.Posts.Remove(post);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Post deleted successfully.", id });
});

// DELETE /api/admin/comments/{id}: Delete any comment
admin.MapDelete("/comments/{id:int}", async (int id, AppDbContext db) =>
{
    var comment = await db.Comments.FindAsync(id);
    if (comment == null)
    {
        return Results.NotFound(new { message = "Comment not found." });
    }

    db.Comments.Remove(comment);
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Comment deleted successfully.", id });
});

app.Run();

static bool FixedTimeEquals(string expected, string actual)
{
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var actualBytes = Encoding.UTF8.GetBytes(actual);
    return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
}

// Helper method to retrieve authenticated and active user
static async Task<User?> GetAuthenticatedActiveUserAsync(HttpContext context, AppDbContext db)
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return null;
    }

    var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out var userId))
    {
        return null;
    }

    var user = await db.Users.FindAsync(userId);
    if (user == null || !user.IsActive)
    {
        return null;
    }

    return user;
}

// Helper method to normalize tags to #AI, #Tech, or #Community
static string NormalizeTag(string? rawTag)
{
    if (string.IsNullOrWhiteSpace(rawTag)) return "#Community";
    var trimmed = rawTag.Trim();
    if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;

    if (string.Equals(trimmed, "#AI", StringComparison.OrdinalIgnoreCase)) return "#AI";
    if (string.Equals(trimmed, "#Tech", StringComparison.OrdinalIgnoreCase)) return "#Tech";
    if (string.Equals(trimmed, "#Community", StringComparison.OrdinalIgnoreCase)) return "#Community";

    return "#Community";
}

public partial class Program { }
