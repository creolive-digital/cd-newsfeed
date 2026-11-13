using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
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
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 6 * 1024 * 1024);
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
builder.Services.AddSignalR();

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
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'ProfileImageUrl')
                ALTER TABLE [Users] ADD [ProfileImageUrl] nvarchar(1000) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'Bio')
                ALTER TABLE [Users] ADD [Bio] nvarchar(500) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'Location')
                ALTER TABLE [Users] ADD [Location] nvarchar(100) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'WebsiteUrl')
                ALTER TABLE [Users] ADD [WebsiteUrl] nvarchar(300) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'Email')
                ALTER TABLE [Users] ADD [Email] nvarchar(254) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'PasswordResetTokenHash')
                ALTER TABLE [Users] ADD [PasswordResetTokenHash] nvarchar(128) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'PasswordResetExpiresAt')
                ALTER TABLE [Users] ADD [PasswordResetExpiresAt] datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'EmailVerifiedAt')
                ALTER TABLE [Users] ADD [EmailVerifiedAt] datetime2 NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'PendingEmail')
                ALTER TABLE [Users] ADD [PendingEmail] nvarchar(254) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'EmailVerificationTokenHash')
                ALTER TABLE [Users] ADD [EmailVerificationTokenHash] nvarchar(128) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'EmailVerificationExpiresAt')
                ALTER TABLE [Users] ADD [EmailVerificationExpiresAt] datetime2 NULL;
            IF OBJECT_ID('Notifications', 'U') IS NULL
            BEGIN
                CREATE TABLE [Notifications] (
                    [Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [RecipientUserId] int NOT NULL,
                    [ActorUserId] int NOT NULL,
                    [PostId] int NULL,
                    [Type] nvarchar(30) NOT NULL,
                    [Preview] nvarchar(300) NULL,
                    [IsRead] bit NOT NULL CONSTRAINT [DF_Notifications_IsRead] DEFAULT 0,
                    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Notifications_CreatedAt] DEFAULT GETUTCDATE()
                );
                CREATE INDEX [IX_Notifications_RecipientUserId_IsRead_CreatedAt] ON [Notifications] ([RecipientUserId], [IsRead], [CreatedAt]);
            END;
            IF OBJECT_ID('PostLikes', 'U') IS NULL
            BEGIN
                CREATE TABLE [PostLikes] (
                    [Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [PostId] int NOT NULL,
                    [UserId] int NOT NULL,
                    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_PostLikes_CreatedAt] DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_PostLikes_Posts_PostId] FOREIGN KEY ([PostId]) REFERENCES [Posts]([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_PostLikes_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id])
                );
                CREATE UNIQUE INDEX [IX_PostLikes_PostId_UserId] ON [PostLikes] ([PostId], [UserId]);
            END;
            IF OBJECT_ID('PostBookmarks', 'U') IS NULL
            BEGIN
                CREATE TABLE [PostBookmarks] (
                    [Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [PostId] int NOT NULL,
                    [UserId] int NOT NULL,
                    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_PostBookmarks_CreatedAt] DEFAULT GETUTCDATE(),
                    CONSTRAINT [FK_PostBookmarks_Posts_PostId] FOREIGN KEY ([PostId]) REFERENCES [Posts]([Id]) ON DELETE CASCADE,
                    CONSTRAINT [FK_PostBookmarks_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id])
                );
                CREATE UNIQUE INDEX [IX_PostBookmarks_UserId_PostId] ON [PostBookmarks] ([UserId], [PostId]);
            END;
            IF OBJECT_ID('PostReports', 'U') IS NULL
            BEGIN
                CREATE TABLE [PostReports] ([Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY, [ReporterUserId] int NOT NULL, [PostId] int NULL, [CommentId] int NULL, [Reason] nvarchar(500) NOT NULL, [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_PostReports_Status] DEFAULT 'Open', [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_PostReports_CreatedAt] DEFAULT GETUTCDATE(), [ResolvedAt] datetime2 NULL);
                CREATE INDEX [IX_PostReports_Status_CreatedAt] ON [PostReports] ([Status], [CreatedAt]);
            END;
            IF OBJECT_ID('UserFollows', 'U') IS NULL
            BEGIN
                CREATE TABLE [UserFollows] ([Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY, [FollowerUserId] int NOT NULL, [FollowedUserId] int NULL, [Topic] nvarchar(50) NULL, [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_UserFollows_CreatedAt] DEFAULT GETUTCDATE());
                CREATE UNIQUE INDEX [IX_UserFollows_FollowerUserId_FollowedUserId_Topic] ON [UserFollows] ([FollowerUserId], [FollowedUserId], [Topic]);
            END;
            IF OBJECT_ID('NotificationPreferences', 'U') IS NULL
                CREATE TABLE [NotificationPreferences] ([UserId] int NOT NULL PRIMARY KEY, [LikesEnabled] bit NOT NULL CONSTRAINT [DF_NotificationPreferences_LikesEnabled] DEFAULT 1, [CommentsEnabled] bit NOT NULL CONSTRAINT [DF_NotificationPreferences_CommentsEnabled] DEFAULT 1, [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_NotificationPreferences_UpdatedAt] DEFAULT GETUTCDATE());
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
app.MapHub<FeedHub>("/hubs/feed").RequireAuthorization();

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
    if (!string.IsNullOrWhiteSpace(req.Email) && !System.Net.Mail.MailAddress.TryCreate(req.Email.Trim(), out _))
        return Results.BadRequest(new { message = "Enter a valid email address." });

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
        PendingEmail = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim().ToLowerInvariant(),
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
        email = user.Email,
        pendingEmail = user.PendingEmail,
        emailVerified = user.EmailVerifiedAt != null,
        profileImageUrl = user.ProfileImageUrl,
        bio = user.Bio,
        location = user.Location,
        websiteUrl = user.WebsiteUrl,
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
        profileImageUrl = adminUser.ProfileImageUrl,
        bio = adminUser.Bio,
        location = adminUser.Location,
        websiteUrl = adminUser.WebsiteUrl,
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
        profileImageUrl = user.ProfileImageUrl,
        bio = user.Bio,
        location = user.Location,
        websiteUrl = user.WebsiteUrl,
        role = user.Role
    });
});

// Password recovery uses short-lived, single-use tokens delivered through configured SMTP.
app.MapPost("/api/auth/password-reset/request", async (PasswordResetRequest req, AppDbContext db, IConfiguration configuration, HttpContext context) =>
{
    var smtpHost = configuration["Smtp:Host"];
    var sender = configuration["Smtp:From"];
    if (string.IsNullOrWhiteSpace(smtpHost) || string.IsNullOrWhiteSpace(sender))
        return Results.Json(new { message = "Password recovery is not configured yet. Please contact the administrator." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    if (!string.IsNullOrWhiteSpace(req.Email))
    {
        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        var account = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail && u.EmailVerifiedAt != null && u.Role != "Admin" && u.IsActive);
        if (account != null)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            account.PasswordResetTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            account.PasswordResetExpiresAt = DateTime.UtcNow.AddMinutes(30);
            await db.SaveChangesAsync();

            var publicBaseUrl = configuration["PublicBaseUrl"]?.TrimEnd('/') ?? $"{context.Request.Scheme}://{context.Request.Host}";
            var resetUrl = $"{publicBaseUrl}/?resetToken={Uri.EscapeDataString(token)}";
            using var message = new System.Net.Mail.MailMessage(sender, account.Email!, "Reset your TechPulse password", $"Use this link within 30 minutes to reset your password:\n\n{resetUrl}\n\nIf you did not request this, ignore this email.");
            using var client = new System.Net.Mail.SmtpClient(smtpHost, int.TryParse(configuration["Smtp:Port"], out var port) ? port : 587)
            {
                EnableSsl = !string.Equals(configuration["Smtp:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase),
                Credentials = string.IsNullOrWhiteSpace(configuration["Smtp:Username"]) ? null : new System.Net.NetworkCredential(configuration["Smtp:Username"], configuration["Smtp:Password"])
            };
            try { await client.SendMailAsync(message, context.RequestAborted); }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException)
            {
                account.PasswordResetTokenHash = null;
                account.PasswordResetExpiresAt = null;
                await db.SaveChangesAsync();
                return Results.Json(new { message = "We could not send the recovery email right now. Please try again later." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }
    }
    return Results.Ok(new { message = "If an active account uses that email, a recovery link will arrive shortly." });
});

app.MapPost("/api/auth/password-reset/confirm", async (PasswordResetConfirmRequest req, AppDbContext db, IPasswordHasher<User> passwordHasher) =>
{
    if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
        return Results.BadRequest(new { message = "Enter a valid recovery link and a password of at least 8 characters." });
    var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Token)));
    var account = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash && u.PasswordResetExpiresAt > DateTime.UtcNow && u.IsActive && u.Role != "Admin");
    if (account == null) return Results.BadRequest(new { message = "This recovery link is invalid or expired. Request a new one." });
    account.PasswordHash = passwordHasher.HashPassword(account, req.NewPassword);
    account.PasswordResetTokenHash = null;
    account.PasswordResetExpiresAt = null;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Your password has been reset. You can now sign in." });
});

app.MapPost("/api/auth/password/change", async (ChangePasswordRequest request, HttpContext httpContext, AppDbContext db, IPasswordHasher<User> passwordHasher) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        return Results.BadRequest(new { message = "Enter your current password and a new password of at least 8 characters." });
    if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        return Results.BadRequest(new { message = "Your current password is incorrect." });
    user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
    user.PasswordResetTokenHash = null;
    user.PasswordResetExpiresAt = null;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Your password has been changed." });
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
        email = user.Email,
        pendingEmail = user.PendingEmail,
        emailVerified = user.EmailVerifiedAt != null,
        profileImageUrl = user.ProfileImageUrl,
        bio = user.Bio,
        location = user.Location,
        websiteUrl = user.WebsiteUrl,
        role = user.Role
    });
});

// PATCH /api/auth/profile: Update the signed-in user's public profile details.
app.MapPatch("/api/auth/profile", async (UpdateProfileRequest req, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();

    var displayName = req.DisplayName?.Trim();
    var bio = req.Bio?.Trim();
    var location = req.Location?.Trim();
    var websiteUrl = req.WebsiteUrl?.Trim();
    var email = req.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 100)
        return Results.BadRequest(new { message = "Display name is required and must be 100 characters or fewer." });
    if (bio?.Length > 500 || location?.Length > 100 || websiteUrl?.Length > 300)
        return Results.BadRequest(new { message = "Bio, location, or website exceeds the allowed length." });
    if (!string.IsNullOrWhiteSpace(email) && !System.Net.Mail.MailAddress.TryCreate(email, out _))
        return Results.BadRequest(new { message = "Enter a valid email address." });
    if (!string.IsNullOrWhiteSpace(email) && await db.Users.AnyAsync(u => u.Id != user.Id && (u.Email == email || u.PendingEmail == email)))
        return Results.Conflict(new { message = "That email address is already linked to another account." });
    if (!string.IsNullOrWhiteSpace(websiteUrl) &&
        (!Uri.TryCreate(websiteUrl, UriKind.Absolute, out var parsedWebsite) ||
         (parsedWebsite.Scheme != Uri.UriSchemeHttp && parsedWebsite.Scheme != Uri.UriSchemeHttps)))
        return Results.BadRequest(new { message = "Website must be a valid HTTP or HTTPS URL." });

    user.DisplayName = displayName;
    user.Bio = string.IsNullOrWhiteSpace(bio) ? null : bio;
    user.Location = string.IsNullOrWhiteSpace(location) ? null : location;
    user.WebsiteUrl = string.IsNullOrWhiteSpace(websiteUrl) ? null : websiteUrl;
    var changedRecoveryEmail = !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase) || user.PendingEmail != null;
    if (changedRecoveryEmail)
    {
        user.PendingEmail = string.IsNullOrWhiteSpace(email) ? null : email;
        user.EmailVerificationTokenHash = null;
        user.EmailVerificationExpiresAt = null;
    }
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        id = user.Id,
        displayName = user.DisplayName,
        username = user.Username,
        email = user.Email,
        pendingEmail = user.PendingEmail,
        emailVerified = user.EmailVerifiedAt != null,
        profileImageUrl = user.ProfileImageUrl,
        bio = user.Bio,
        location = user.Location,
        websiteUrl = user.WebsiteUrl,
        role = user.Role
    });
});

app.MapPost("/api/auth/email/verification/request", async (HttpContext httpContext, AppDbContext db, IConfiguration configuration) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(user.PendingEmail)) return Results.BadRequest(new { message = "Add a recovery email to your profile first." });
    var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    user.EmailVerificationTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    user.EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24);
    await db.SaveChangesAsync();
    var publicBaseUrl = configuration["PublicBaseUrl"]?.TrimEnd('/') ?? $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    var link = $"{publicBaseUrl}/?verifyEmailToken={Uri.EscapeDataString(token)}";
    var sent = await SendConfiguredEmailAsync(configuration, user.PendingEmail, "Verify your TechPulse recovery email", $"Verify your recovery email within 24 hours:\n\n{link}\n\nIf you did not request this, ignore this email.", httpContext.RequestAborted);
    if (!sent) return Results.Json(new { message = "Email delivery is not configured. Ask the administrator to configure SMTP." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(new { message = "Verification email sent. Check your inbox." });
});

app.MapPost("/api/auth/email/verification/confirm", async (PasswordResetConfirmRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Token)) return Results.BadRequest(new { message = "Verification link is invalid." });
    var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
    var user = await db.Users.FirstOrDefaultAsync(u => u.EmailVerificationTokenHash == tokenHash && u.EmailVerificationExpiresAt > DateTime.UtcNow && u.PendingEmail != null && u.IsActive);
    if (user == null) return Results.BadRequest(new { message = "This verification link is invalid or expired." });
    if (await db.Users.AnyAsync(u => u.Id != user.Id && u.Email == user.PendingEmail)) return Results.Conflict(new { message = "That email address is already in use." });
    user.Email = user.PendingEmail;
    user.EmailVerifiedAt = DateTime.UtcNow;
    user.PendingEmail = null;
    user.EmailVerificationTokenHash = null;
    user.EmailVerificationExpiresAt = null;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Recovery email verified. You can use it to reset your password." });
});

// POST /api/auth/profile/image: Upload a small PNG, JPEG, or WebP avatar.
app.MapPost("/api/auth/profile/image", async (HttpContext httpContext, AppDbContext db, IWebHostEnvironment environment) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    if (!httpContext.Request.HasFormContentType)
        return Results.BadRequest(new { message = "Upload an image using multipart form data." });

    var form = await httpContext.Request.ReadFormAsync();
    var file = form.Files.GetFile("image");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { message = "Select a profile image to upload." });
    if (file.Length > 2 * 1024 * 1024)
        return Results.BadRequest(new { message = "Profile images must be 2 MB or smaller." });

    var imageBytes = new byte[(int)file.Length];
    await using (var stream = file.OpenReadStream())
    {
        await stream.ReadExactlyAsync(imageBytes);
    }

    var extension = DetectImageExtension(imageBytes);
    if (extension == null)
        return Results.BadRequest(new { message = "Use a valid PNG, JPEG, or WebP image." });

    var profileDirectory = Path.Combine(environment.WebRootPath, "uploads", "profiles");
    Directory.CreateDirectory(profileDirectory);
    var fileName = $"{user.Id}_{Guid.NewGuid():N}{extension}";
    await File.WriteAllBytesAsync(Path.Combine(profileDirectory, fileName), imageBytes);

    var oldImageUrl = user.ProfileImageUrl;
    user.ProfileImageUrl = $"/uploads/profiles/{fileName}";
    await db.SaveChangesAsync();

    if (oldImageUrl?.StartsWith("/uploads/profiles/", StringComparison.Ordinal) == true)
    {
        var oldFileName = Path.GetFileName(oldImageUrl);
        var oldImagePath = Path.Combine(profileDirectory, oldFileName);
        if (!string.Equals(oldFileName, fileName, StringComparison.Ordinal) && File.Exists(oldImagePath))
            File.Delete(oldImagePath);
    }

    return Results.Ok(new { profileImageUrl = user.ProfileImageUrl });
});

// GET /api/notifications: Recent activity for the current user.
app.MapGet("/api/notifications/preferences", async (HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var preferences = await db.NotificationPreferences.FindAsync(user.Id);
    return Results.Ok(new { likesEnabled = preferences?.LikesEnabled ?? true, commentsEnabled = preferences?.CommentsEnabled ?? true });
});

app.MapPatch("/api/notifications/preferences", async (UpdateNotificationPreferencesRequest request, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var preferences = await db.NotificationPreferences.FindAsync(user.Id);
    if (preferences == null)
    {
        preferences = new NotificationPreference { UserId = user.Id };
        db.NotificationPreferences.Add(preferences);
    }
    preferences.LikesEnabled = request.LikesEnabled;
    preferences.CommentsEnabled = request.CommentsEnabled;
    preferences.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { likesEnabled = preferences.LikesEnabled, commentsEnabled = preferences.CommentsEnabled });
});

app.MapGet("/api/notifications", async (HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();

    var items = await (
        from notification in db.Notifications.AsNoTracking()
        where notification.RecipientUserId == user.Id
        join actor in db.Users.AsNoTracking() on notification.ActorUserId equals actor.Id
        join postItem in db.Posts.AsNoTracking() on notification.PostId equals (int?)postItem.Id into postJoin
        from post in postJoin.DefaultIfEmpty()
        orderby notification.CreatedAt descending
        select new
        {
            id = notification.Id,
            type = notification.Type,
            preview = notification.Preview,
            isRead = notification.IsRead,
            createdAt = notification.CreatedAt,
            actor = new
            {
                id = actor.Id,
                displayName = actor.DisplayName,
                username = actor.Username,
                profileImageUrl = actor.ProfileImageUrl
            },
            postId = notification.PostId,
            postTitle = post == null ? null : post.SinhalaTitle
        })
        .Take(30)
        .ToListAsync();
    var unreadCount = await db.Notifications.CountAsync(n => n.RecipientUserId == user.Id && !n.IsRead);
    return Results.Ok(new { unreadCount, items });
});

app.MapGet("/api/follows", async (HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var follows = await db.UserFollows.AsNoTracking().Where(f => f.FollowerUserId == user.Id)
        .Select(f => new { id = f.Id, userId = f.FollowedUserId, topic = f.Topic }).ToListAsync();
    return Results.Ok(follows);
});

app.MapPost("/api/follows/users/{id:int}", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    if (id == user.Id) return Results.BadRequest(new { message = "You cannot follow your own account." });
    if (!await db.Users.AnyAsync(u => u.Id == id && u.IsActive)) return Results.NotFound();
    var existing = await db.UserFollows.FirstOrDefaultAsync(f => f.FollowerUserId == user.Id && f.FollowedUserId == id);
    var followed = existing == null;
    if (existing == null) db.UserFollows.Add(new UserFollow { FollowerUserId = user.Id, FollowedUserId = id, CreatedAt = DateTime.UtcNow });
    else db.UserFollows.Remove(existing);
    await db.SaveChangesAsync();
    return Results.Ok(new { followed });
});

app.MapPost("/api/follows/topics/{tag}", async (string tag, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var topic = NormalizeTag(tag);
    if (topic == "#Community" && !string.Equals(tag.TrimStart('#'), "Community", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "Choose AI, Tech, or Community." });
    var existing = await db.UserFollows.FirstOrDefaultAsync(f => f.FollowerUserId == user.Id && f.FollowedUserId == null && f.Topic == topic);
    var followed = existing == null;
    if (existing == null) db.UserFollows.Add(new UserFollow { FollowerUserId = user.Id, Topic = topic, CreatedAt = DateTime.UtcNow });
    else db.UserFollows.Remove(existing);
    await db.SaveChangesAsync();
    return Results.Ok(new { followed, topic });
});

// GET /api/auth/activity: The signed-in user's post history and engagement totals.
app.MapGet("/api/auth/activity", async (HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();

    var posts = await db.Posts.AsNoTracking()
        .Where(p => p.AuthorId == user.Id)
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new
        {
            id = p.Id,
            title = p.SinhalaTitle,
            summary = p.SinhalaSummary,
            content = p.SinhalaContent,
            imageUrl = p.ImageUrl,
            link = p.Link,
            tag = p.Tag,
            status = p.Status,
            createdAt = p.CreatedAt,
            likes = db.PostLikes.Count(l => l.PostId == p.Id),
            comments = p.Comments.Count
        })
        .Take(10)
        .ToListAsync();
    var postCount = await db.Posts.CountAsync(p => p.AuthorId == user.Id);
    var likesReceived = await db.PostLikes.CountAsync(l => l.Post.AuthorId == user.Id);
    var commentsReceived = await db.Comments.CountAsync(c => c.Post.AuthorId == user.Id);

    return Results.Ok(new { postCount, likesReceived, commentsReceived, posts });
});

app.MapGet("/api/posts/{id:int}/edit", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var post = await db.Posts.AsNoTracking().Where(p => p.Id == id && p.AuthorId == user.Id && !p.IsAutoNews)
        .Select(p => new { id = p.Id, title = p.SinhalaTitle, summary = p.SinhalaSummary, content = p.SinhalaContent, tag = p.Tag, imageUrl = p.ImageUrl, link = p.Link, status = p.Status })
        .FirstOrDefaultAsync();
    return post == null ? Results.NotFound() : Results.Ok(post);
});

// GET /api/users/{id}/profile: Public profile details and recent approved community posts.
app.MapGet("/api/users/{id:int}/profile", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var viewer = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (viewer == null) return Results.Unauthorized();

    var profile = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive);
    if (profile == null) return Results.NotFound(new { message = "User profile not found." });

    var posts = await db.Posts.AsNoTracking()
        .Where(p => p.AuthorId == id && p.Status == "Approved")
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new
        {
            id = p.Id,
            title = p.SinhalaTitle,
            summary = p.SinhalaSummary,
            imageUrl = p.ImageUrl,
            tag = p.Tag,
            createdAt = p.CreatedAt,
            likes = db.PostLikes.Count(l => l.PostId == p.Id),
            comments = p.Comments.Count
        })
        .Take(12)
        .ToListAsync();

    return Results.Ok(new
    {
        id = profile.Id,
        displayName = profile.DisplayName,
        username = profile.Username,
        profileImageUrl = profile.ProfileImageUrl,
        bio = profile.Bio,
        location = profile.Location,
        websiteUrl = profile.WebsiteUrl,
        postCount = await db.Posts.CountAsync(p => p.AuthorId == id && p.Status == "Approved"),
        likesReceived = await db.PostLikes.CountAsync(l => l.Post.AuthorId == id),
        commentsReceived = await db.Comments.CountAsync(c => c.Post.AuthorId == id),
        posts
    });
});

// PATCH /api/notifications/read-all
app.MapPatch("/api/notifications/read-all", async (HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var unread = await db.Notifications.Where(n => n.RecipientUserId == user.Id && !n.IsRead).ToListAsync();
    foreach (var notification in unread) notification.IsRead = true;
    if (unread.Count > 0) await db.SaveChangesAsync();
    return Results.Ok(new { markedRead = unread.Count });
});

// PATCH /api/notifications/{id}/read
app.MapPatch("/api/notifications/{id:int}/read", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.RecipientUserId == user.Id);
    if (notification == null) return Results.NotFound();
    notification.IsRead = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { id = notification.Id, isRead = true });
});

// GET /api/feed (Requires authenticated & active user)
app.MapGet("/api/feed", async (HttpContext httpContext, AppDbContext db, int page = 1, int pageSize = 20, bool followingOnly = false, string? search = null, string? sort = "newest", string? tag = null) =>
{
    var currentUser = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (currentUser == null)
    {
        return httpContext.User.Identity?.IsAuthenticated == true
            ? Results.Json(new { message = "Your account has been deactivated. Please contact an administrator." }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Unauthorized();
    }

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 50);
    var query = db.Posts
        .AsNoTracking()
        .Where(p => p.Status == "Approved")
        ;
    if (!string.IsNullOrWhiteSpace(tag) && !string.Equals(tag, "All", StringComparison.OrdinalIgnoreCase))
    {
        var normalizedTag = NormalizeTag(tag);
        query = query.Where(p => p.Tag == normalizedTag);
    }
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        if (term.Length > 100) term = term[..100];
        query = query.Where(p => p.SinhalaTitle.Contains(term) || p.SinhalaSummary.Contains(term) || p.Source.Contains(term) || p.Tag.Contains(term) || (p.Author != null && p.Author.DisplayName.Contains(term)));
    }
    if (followingOnly)
    {
        query = query.Where(p => db.UserFollows.Any(f => f.FollowerUserId == currentUser.Id &&
            ((f.FollowedUserId.HasValue && f.FollowedUserId == p.AuthorId) || (f.Topic != null && f.Topic == p.Tag))));
    }
    var total = await query.CountAsync();
    var orderedQuery = sort?.ToLowerInvariant() switch
    {
        "oldest" => query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
        "popular" => query.OrderByDescending(p => db.PostLikes.Count(l => l.PostId == p.Id)).ThenByDescending(p => p.CreatedAt),
        _ => query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
    };
    var posts = await orderedQuery
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
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
            likeCount = db.PostLikes.Count(l => l.PostId == p.Id),
            likedByCurrentUser = db.PostLikes.Any(l => l.PostId == p.Id && l.UserId == currentUser.Id),
            bookmarkedByCurrentUser = db.PostBookmarks.Any(b => b.PostId == p.Id && b.UserId == currentUser.Id),
            followedAuthorByCurrentUser = p.AuthorId.HasValue && db.UserFollows.Any(f => f.FollowerUserId == currentUser.Id && f.FollowedUserId == p.AuthorId),
            followedTopicByCurrentUser = db.UserFollows.Any(f => f.FollowerUserId == currentUser.Id && f.Topic == p.Tag),
            author = p.Author == null ? null : new
            {
                id = p.Author.Id,
                displayName = p.Author.DisplayName,
                username = p.Author.Username,
                profileImageUrl = p.Author.ProfileImageUrl
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
                        username = c.User.Username,
                        profileImageUrl = c.User.ProfileImageUrl
                    }
                }).ToList()
        })
        .ToListAsync();

    return Results.Ok(new { items = posts, page, pageSize, total, hasMore = page * pageSize < total });
});

// POST /api/posts/{id}/bookmark: Toggle a saved post for the current user.
app.MapPost("/api/posts/{id:int}/bookmark", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var currentUser = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (currentUser == null)
    {
        return httpContext.User.Identity?.IsAuthenticated == true
            ? Results.Json(new { message = "Your account has been deactivated. Please contact an administrator." }, statusCode: StatusCodes.Status403Forbidden)
            : Results.Unauthorized();
    }

    var postExists = await db.Posts.AnyAsync(p => p.Id == id && p.Status == "Approved");
    if (!postExists) return Results.NotFound(new { message = "Post not found." });

    var bookmark = await db.PostBookmarks.FirstOrDefaultAsync(b => b.PostId == id && b.UserId == currentUser.Id);
    var isBookmarked = bookmark == null;
    if (bookmark != null) db.PostBookmarks.Remove(bookmark);
    else db.PostBookmarks.Add(new PostBookmark { PostId = id, UserId = currentUser.Id, CreatedAt = DateTime.UtcNow });
    await db.SaveChangesAsync();
    return Results.Ok(new { bookmarked = isBookmarked });
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

    if (string.IsNullOrWhiteSpace(req.SinhalaTitle) || string.IsNullOrWhiteSpace(req.SinhalaSummary) ||
        req.SinhalaTitle.Length > 500 || req.SinhalaSummary.Length > 10000 || req.SinhalaContent?.Length > 100000)
    {
        return Results.BadRequest(new { message = "A title (up to 500 characters) and summary (up to 10,000 characters) are required; article content must be 100,000 characters or fewer." });
    }

    var tag = NormalizeTag(req.Tag);

    var post = new Post
    {
        SinhalaTitle = req.SinhalaTitle.Trim(),
        SinhalaSummary = req.SinhalaSummary.Trim(),
        SinhalaContent = string.IsNullOrWhiteSpace(req.SinhalaContent) ? null : req.SinhalaContent.Trim(),
        ImageUrl = NormalizeOptionalHttpUrl(req.ImageUrl, allowRelativeUpload: true),
        Tag = tag,
        Link = NormalizeOptionalHttpUrl(req.Link),
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
            username = currentUser.Username,
            profileImageUrl = currentUser.ProfileImageUrl
        }
    });
});

app.MapPost("/api/posts/images", async (HttpContext httpContext, IWebHostEnvironment environment, AppDbContext db) =>
{
    if (await GetAuthenticatedActiveUserAsync(httpContext, db) == null) return Results.Unauthorized();
    var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
    var file = form.Files.GetFile("image");
    if (file == null || file.Length == 0) return Results.BadRequest(new { message = "Choose an image to upload." });
    if (file.Length > 5 * 1024 * 1024) return Results.BadRequest(new { message = "Post images must be 5 MB or smaller." });
    await using var input = file.OpenReadStream();
    using var memory = new MemoryStream();
    await input.CopyToAsync(memory, httpContext.RequestAborted);
    var bytes = memory.ToArray();
    var extension = DetectImageExtension(bytes);
    if (extension == null) return Results.BadRequest(new { message = "Upload a valid PNG, JPEG, or WebP image." });
    var uploadDirectory = Path.Combine(environment.WebRootPath, "uploads", "posts");
    Directory.CreateDirectory(uploadDirectory);
    var fileName = $"{Guid.NewGuid():N}{extension}";
    await File.WriteAllBytesAsync(Path.Combine(uploadDirectory, fileName), bytes, httpContext.RequestAborted);
    return Results.Ok(new { imageUrl = $"/uploads/posts/{fileName}" });
});

app.MapPatch("/api/posts/{id:int}", async (int id, UpdatePostRequest request, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id && p.AuthorId == user.Id && !p.IsAutoNews);
    if (post == null) return Results.NotFound(new { message = "Your post was not found." });
    if (string.IsNullOrWhiteSpace(request.SinhalaTitle) || request.SinhalaTitle.Trim().Length > 500 || string.IsNullOrWhiteSpace(request.SinhalaSummary) || request.SinhalaSummary.Length > 10000)
        return Results.BadRequest(new { message = "A title (up to 500 characters) and summary (up to 10,000 characters) are required." });
    if (request.SinhalaContent?.Length > 100000) return Results.BadRequest(new { message = "Article content is too long." });
    post.SinhalaTitle = request.SinhalaTitle.Trim();
    post.SinhalaSummary = request.SinhalaSummary.Trim();
    post.SinhalaContent = string.IsNullOrWhiteSpace(request.SinhalaContent) ? null : request.SinhalaContent.Trim();
    post.Tag = NormalizeTag(request.Tag);
    post.Link = NormalizeOptionalHttpUrl(request.Link);
    post.ImageUrl = NormalizeOptionalHttpUrl(request.ImageUrl, allowRelativeUpload: true);
    post.Status = "Pending";
    await db.SaveChangesAsync();
    return Results.Ok(new { id = post.Id, status = post.Status, message = "Your edit was submitted for admin review." });
});

app.MapDelete("/api/posts/{id:int}", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id && p.AuthorId == user.Id && !p.IsAutoNews);
    if (post == null) return Results.NotFound(new { message = "Your post was not found." });
    db.Posts.Remove(post);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Post deleted." });
});

app.MapPatch("/api/comments/{id:int}", async (int id, CreateCommentRequest request, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id);
    if (comment == null) return Results.NotFound(new { message = "Your comment was not found." });
    var content = request.Content?.Trim();
    if (string.IsNullOrWhiteSpace(content) || content.Length > 2000) return Results.BadRequest(new { message = "Comment must be between 1 and 2,000 characters." });
    comment.Content = content;
    await db.SaveChangesAsync();
    return Results.Ok(new { id = comment.Id, content = comment.Content, updatedAt = DateTime.UtcNow });
});

app.MapDelete("/api/comments/{id:int}", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id);
    if (comment == null) return Results.NotFound(new { message = "Your comment was not found." });
    db.Comments.Remove(comment);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Comment deleted." });
});

app.MapPost("/api/reports", async (CreateReportRequest request, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();
    if (request.PostId.HasValue == request.CommentId.HasValue) return Results.BadRequest(new { message = "Choose one post or comment to report." });
    var reason = request.Reason?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) return Results.BadRequest(new { message = "Add a reason (up to 500 characters)." });
    if (request.PostId.HasValue && !await db.Posts.AnyAsync(p => p.Id == request.PostId && p.Status == "Approved")) return Results.NotFound();
    if (request.CommentId.HasValue && !await db.Comments.AnyAsync(c => c.Id == request.CommentId)) return Results.NotFound();
    if (await db.PostReports.AnyAsync(r => r.ReporterUserId == user.Id && r.Status == "Open" && r.PostId == request.PostId && r.CommentId == request.CommentId))
        return Results.Conflict(new { message = "You have already reported this content. The moderation team will review it." });
    var report = new PostReport { ReporterUserId = user.Id, PostId = request.PostId, CommentId = request.CommentId, Reason = reason, CreatedAt = DateTime.UtcNow };
    db.PostReports.Add(report);
    await db.SaveChangesAsync();
    return Results.Created($"/api/reports/{report.Id}", new { report.Id, report.Status });
});

// POST /api/posts/{id}/like: Toggle the current user's like on an approved post.
app.MapPost("/api/posts/{id:int}/like", async (int id, HttpContext httpContext, AppDbContext db) =>
{
    var user = await GetAuthenticatedActiveUserAsync(httpContext, db);
    if (user == null) return Results.Unauthorized();

    var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id && p.Status == "Approved");
    if (post == null) return Results.NotFound(new { message = "Post not found." });

    var existingLike = await db.PostLikes.FirstOrDefaultAsync(l => l.PostId == id && l.UserId == user.Id);
    var liked = existingLike == null;
    if (existingLike != null)
    {
        db.PostLikes.Remove(existingLike);
    }
    else
    {
        db.PostLikes.Add(new PostLike { PostId = id, UserId = user.Id, CreatedAt = DateTime.UtcNow });
        if (post.AuthorId.HasValue && post.AuthorId.Value != user.Id &&
            (await db.NotificationPreferences.FindAsync(post.AuthorId.Value))?.LikesEnabled != false)
        {
            db.Notifications.Add(new Notification
            {
                RecipientUserId = post.AuthorId.Value,
                ActorUserId = user.Id,
                PostId = post.Id,
                Type = "Like",
                Preview = post.SinhalaTitle.Length > 300 ? post.SinhalaTitle[..300] : post.SinhalaTitle,
                CreatedAt = DateTime.UtcNow
            });
        }
    }
    await db.SaveChangesAsync();

    var likeCount = await db.PostLikes.CountAsync(l => l.PostId == id);
    return Results.Ok(new { postId = id, liked, likeCount });
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
    if (post.AuthorId.HasValue && post.AuthorId.Value != currentUser.Id &&
        (await db.NotificationPreferences.FindAsync(post.AuthorId.Value))?.CommentsEnabled != false)
    {
        db.Notifications.Add(new Notification
        {
            RecipientUserId = post.AuthorId.Value,
            ActorUserId = currentUser.Id,
            PostId = post.Id,
            Type = "Comment",
            Preview = content.Length > 300 ? content[..300] : content,
            CreatedAt = DateTime.UtcNow
        });
    }
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
            username = currentUser.Username,
            profileImageUrl = currentUser.ProfileImageUrl
        }
    });
});

// ==========================================
// Admin Endpoints (Require Role == "Admin")
// ==========================================
var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

admin.MapGet("/insights", async (AppDbContext db) =>
{
    var since = DateTime.UtcNow.AddDays(-30);
    return Results.Ok(new
    {
        totalUsers = await db.Users.CountAsync(u => u.Role != "Admin"),
        activeUsers = await db.Users.CountAsync(u => u.Role != "Admin" && u.IsActive),
        newUsersLast30Days = await db.Users.CountAsync(u => u.Role != "Admin" && u.CreatedAt >= since),
        totalPosts = await db.Posts.CountAsync(p => p.Status == "Approved"),
        pendingPosts = await db.Posts.CountAsync(p => p.Status == "Pending"),
        postsLast30Days = await db.Posts.CountAsync(p => p.CreatedAt >= since && p.Status == "Approved"),
        totalComments = await db.Comments.CountAsync(),
        openReports = await db.PostReports.CountAsync(r => r.Status == "Open"),
        popularTopics = await db.Posts.Where(p => p.Status == "Approved").GroupBy(p => p.Tag)
            .Select(group => new { topic = group.Key, posts = group.Count() }).OrderByDescending(group => group.posts).Take(5).ToListAsync()
    });
});

admin.MapGet("/reports", async (AppDbContext db) =>
{
    var reports = await db.PostReports.AsNoTracking().OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAt)
        .Select(r => new
        {
            id = r.Id, reporterUserId = r.ReporterUserId, postId = r.PostId, commentId = r.CommentId,
            reason = r.Reason, status = r.Status, createdAt = r.CreatedAt,
            postTitle = r.PostId.HasValue ? db.Posts.Where(p => p.Id == r.PostId.Value).Select(p => p.SinhalaTitle).FirstOrDefault() : null,
            commentContent = r.CommentId.HasValue ? db.Comments.Where(c => c.Id == r.CommentId.Value).Select(c => c.Content).FirstOrDefault() : null,
            reporter = db.Users.Where(u => u.Id == r.ReporterUserId).Select(u => u.Username).FirstOrDefault()
        }).ToListAsync();
    return Results.Ok(reports);
});

admin.MapPatch("/reports/{id:int}/resolve", async (int id, AppDbContext db) =>
{
    var report = await db.PostReports.FindAsync(id);
    if (report == null) return Results.NotFound();
    report.Status = "Resolved";
    report.ResolvedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { report.Id, report.Status, report.ResolvedAt });
});

admin.MapDelete("/reports/{id:int}/content", async (int id, AppDbContext db) =>
{
    var report = await db.PostReports.FindAsync(id);
    if (report == null) return Results.NotFound();
    if (report.PostId.HasValue)
    {
        var post = await db.Posts.FindAsync(report.PostId.Value);
        if (post != null) db.Posts.Remove(post);
    }
    else if (report.CommentId.HasValue)
    {
        var comment = await db.Comments.FindAsync(report.CommentId.Value);
        if (comment != null) db.Comments.Remove(comment);
    }
    report.Status = "Resolved";
    report.ResolvedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Reported content removed and report resolved." });
});

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
            profileImageUrl = u.ProfileImageUrl,
            bio = u.Bio,
            location = u.Location,
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
        profileImageUrl = user.ProfileImageUrl,
        bio = user.Bio,
        location = user.Location,
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
                username = p.Author.Username,
                profileImageUrl = p.Author.ProfileImageUrl,
                bio = p.Author.Bio,
                location = p.Author.Location,
                websiteUrl = p.Author.WebsiteUrl
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
            username = post.Author.Username,
            profileImageUrl = post.Author.ProfileImageUrl,
            bio = post.Author.Bio,
            location = post.Author.Location,
            websiteUrl = post.Author.WebsiteUrl
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

static async Task<bool> SendConfiguredEmailAsync(IConfiguration configuration, string recipient, string subject, string body, CancellationToken cancellationToken)
{
    var host = configuration["Smtp:Host"];
    var sender = configuration["Smtp:From"];
    if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender)) return false;
    try
    {
        using var message = new System.Net.Mail.MailMessage(sender, recipient, subject, body);
        using var client = new System.Net.Mail.SmtpClient(host, int.TryParse(configuration["Smtp:Port"], out var port) ? port : 587)
        {
            EnableSsl = !string.Equals(configuration["Smtp:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase),
            Credentials = string.IsNullOrWhiteSpace(configuration["Smtp:Username"]) ? null : new System.Net.NetworkCredential(configuration["Smtp:Username"], configuration["Smtp:Password"])
        };
        await client.SendMailAsync(message, cancellationToken);
        return true;
    }
    catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException or ArgumentException)
    {
        return false;
    }
}

static string? DetectImageExtension(byte[] bytes)
{
    if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
    if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
    if (bytes.Length >= 12 &&
        bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
        bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
    return null;
}

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

static string? NormalizeOptionalHttpUrl(string? value, bool allowRelativeUpload = false)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var trimmed = value.Trim();
    if (allowRelativeUpload && trimmed.StartsWith("/uploads/posts/", StringComparison.Ordinal) &&
        !trimmed.Contains("..", StringComparison.Ordinal)) return trimmed;
    return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.ToString() : null;
}

public partial class Program { }
