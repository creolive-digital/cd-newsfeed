using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NewsFeed.Tests;

public sealed class NewsFeedApplicationFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _testConfiguration = new()
    {
        ["USE_INMEMORY_DB"] = "true",
        ["INMEMORY_DB_NAME"] = $"NewsFeedTests-{Guid.NewGuid():N}",
        ["Gemini:ApiKeys:0"] = "",
        ["AdminAuth:Username"] = "qa-admin",
        ["AdminAuth:Password"] = "TestOnly-Admin-Password-2026!",
        ["AdminAuth:SessionVersion"] = "1",
        ["Cors:AllowedOrigins:0"] = "https://client.example.test"
    };

    public void SetAdminSessionVersion(string version) => Services.GetRequiredService<IConfiguration>()["AdminAuth:SessionVersion"] = version;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        builder.UseContentRoot(appRoot);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(_testConfiguration);
        });
    }
}
