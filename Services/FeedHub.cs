using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using cd_newsfeed.Data;

namespace cd_newsfeed.Services;

public sealed class FeedHub(AppDbContext db) : Hub
{
    public const string FeedGroup = "feed:all";
    public static string UserGroup(int userId) => $"feed:user:{userId}";
    public const string AdminGroup = "feed:admins";

    public override async Task OnConnectedAsync()
    {
        var userIdValue = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId) || !await db.Users.AnyAsync(user => user.Id == userId && user.IsActive))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, FeedGroup);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        if (Context.User?.IsInRole("Admin") == true)
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        await base.OnConnectedAsync();
    }
}
