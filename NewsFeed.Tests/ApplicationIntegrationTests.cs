using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NewsFeed.Tests;

public class ApplicationIntegrationTests
{
    [Fact]
    public async Task Root_and_manifest_serve_pwa_files()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();

        using var homeResponse = await client.GetAsync("/");
        using var manifestResponse = await client.GetAsync("/manifest.json");

        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        Assert.Equal("application/json", manifestResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Feed_requires_authentication()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/feed");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task First_registered_user_is_admin_and_can_load_feed()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();
        var uniqueUsername = $"first_admin_{Guid.NewGuid():N}";

        using var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            displayName = "First Admin",
            username = uniqueUsername,
            password = "StrongTestPassword123!"
        });

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        using var registrationJson = JsonDocument.Parse(await registerResponse.Content.ReadAsStringAsync());
        Assert.Equal("Admin", registrationJson.RootElement.GetProperty("role").GetString());

        using var feedResponse = await client.GetAsync("/api/feed");
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
        Assert.Equal("application/json", feedResponse.Content.Headers.ContentType?.MediaType);
    }
}
