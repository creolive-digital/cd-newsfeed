using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using cd_newsfeed.Models;
using cd_newsfeed.Services;

namespace cd_newsfeed.Data;

public class AppDbContext : DbContext
{
    private readonly IHubContext<FeedHub>? _hub;

    public AppDbContext(DbContextOptions<AppDbContext> options, IHubContext<FeedHub>? hub = null) : base(options)
    {
        _hub = hub;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<PostLike> PostLikes => Set<PostLike>();
    public DbSet<PostBookmark> PostBookmarks => Set<PostBookmark>();
    public DbSet<PostReport> PostReports => Set<PostReport>();
    public DbSet<UserFollow> UserFollows => Set<UserFollow>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var changedEntities = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity)
            .ToList();
        var notifications = changedEntities.OfType<Notification>().Select(item => item.RecipientUserId).Distinct().ToList();
        var bookmarkUsers = changedEntities.OfType<PostBookmark>().Select(item => item.UserId).Distinct().ToList();
        var followUsers = changedEntities.OfType<UserFollow>().Select(item => item.FollowerUserId).Distinct().ToList();
        var feedChanged = changedEntities.Any(entity => entity is User or Post or Comment or PostLike);
        var moderationChanged = changedEntities.Any(entity => entity is PostReport || entity is Post { Status: "Pending" or "Approved" or "Rejected" });

        var result = await base.SaveChangesAsync(cancellationToken);
        if (_hub == null) return result;

        if (feedChanged)
            await TryPublishAsync(() => _hub.Clients.Group(FeedHub.FeedGroup).SendAsync("FeedChanged", cancellationToken: CancellationToken.None));
        if (moderationChanged)
            await TryPublishAsync(() => _hub.Clients.Group(FeedHub.AdminGroup).SendAsync("ModerationChanged", cancellationToken: CancellationToken.None));
        foreach (var userId in notifications)
            await TryPublishAsync(() => _hub.Clients.Group(FeedHub.UserGroup(userId)).SendAsync("NotificationsChanged", cancellationToken: CancellationToken.None));
        foreach (var userId in bookmarkUsers.Concat(followUsers).Distinct())
            await TryPublishAsync(() => _hub.Clients.Group(FeedHub.UserGroup(userId)).SendAsync("FeedChanged", cancellationToken: CancellationToken.None));

        return result;
    }

    private static async Task TryPublishAsync(Func<Task> publish)
    {
        try { await publish(); }
        catch { /* A live-update transport failure must not undo a committed database write. */ }
    }
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Users configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.Username).IsRequired().HasMaxLength(50);
            entity.Property(u => u.Email).HasMaxLength(254);
            entity.Property(u => u.PendingEmail).HasMaxLength(254);
            entity.Property(u => u.PasswordResetTokenHash).HasMaxLength(128);
            entity.Property(u => u.EmailVerificationTokenHash).HasMaxLength(128);
            entity.Property(u => u.DisplayName).IsRequired().HasMaxLength(100);
            entity.Property(u => u.ProfileImageUrl).HasMaxLength(1000);
            entity.Property(u => u.Bio).HasMaxLength(500);
            entity.Property(u => u.Location).HasMaxLength(100);
            entity.Property(u => u.WebsiteUrl).HasMaxLength(300);
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.Role).IsRequired().HasMaxLength(20);
            entity.Property(u => u.IsActive).HasDefaultValue(true);
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasMany(u => u.Posts)
                .WithOne(p => p.Author)
                .HasForeignKey(p => p.AuthorId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(u => u.Comments)
                .WithOne(c => c.User)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Posts configuration
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.SinhalaTitle).IsRequired().HasMaxLength(500);
            entity.Property(p => p.SinhalaSummary).IsRequired();
            entity.Property(p => p.Source).IsRequired().HasMaxLength(200);
            entity.Property(p => p.ImageUrl).HasMaxLength(1000);
            entity.Property(p => p.SinhalaContent);
            entity.Property(p => p.Tag).IsRequired().HasMaxLength(50);
            entity.Property(p => p.Status).IsRequired().HasMaxLength(50);
            entity.Property(p => p.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            entity.HasMany(p => p.Comments)
                .WithOne(c => c.Post)
                .HasForeignKey(c => c.PostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Comments configuration
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Content).IsRequired().HasMaxLength(2000);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        modelBuilder.Entity<PostLike>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => new { l.PostId, l.UserId }).IsUnique();
            entity.Property(l => l.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.HasOne(l => l.Post).WithMany().HasForeignKey(l => l.PostId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.User).WithMany(u => u.Likes).HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PostBookmark>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.HasIndex(b => new { b.UserId, b.PostId }).IsUnique();
            entity.Property(b => b.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.HasOne(b => b.Post).WithMany().HasForeignKey(b => b.PostId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.User).WithMany(u => u.Bookmarks).HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => new { n.RecipientUserId, n.IsRead, n.CreatedAt });
            entity.Property(n => n.Type).IsRequired().HasMaxLength(30);
            entity.Property(n => n.Preview).HasMaxLength(300);
            entity.Property(n => n.IsRead).HasDefaultValue(false);
            entity.Property(n => n.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        modelBuilder.Entity<PostReport>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.Status, r.CreatedAt });
            entity.Property(r => r.Reason).IsRequired().HasMaxLength(500);
            entity.Property(r => r.Status).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<UserFollow>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => new { f.FollowerUserId, f.FollowedUserId, f.Topic }).IsUnique();
            entity.Property(f => f.Topic).HasMaxLength(50);
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.HasKey(p => p.UserId);
        });
    }
}
