using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace NewsFeed.Tests;

public sealed class NewsFeedApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        builder.UseContentRoot(appRoot);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["USE_INMEMORY_DB"] = "true",
                ["INMEMORY_DB_NAME"] = $"NewsFeedTests-{Guid.NewGuid():N}"
            });
        });
    }
}
