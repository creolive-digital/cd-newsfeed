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
        using var stylesResponse = await client.GetAsync("/css/app.min.css");
        using var signalRResponse = await client.GetAsync("/js/signalr.min.js");

        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        Assert.Equal("application/json", manifestResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, stylesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signalRResponse.StatusCode);
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
    public async Task Registered_user_gets_user_role_and_can_load_feed()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();
        var uniqueUsername = $"member_{Guid.NewGuid():N}";

        using var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            displayName = "Feed Member",
            username = uniqueUsername,
            password = "StrongTestPassword123!"
        });

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        using var registrationJson = JsonDocument.Parse(await registerResponse.Content.ReadAsStringAsync());
        Assert.Equal("User", registrationJson.RootElement.GetProperty("role").GetString());

        using var feedResponse = await client.GetAsync("/api/feed");
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
        Assert.Equal("application/json", feedResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_has_dedicated_login_and_role_protected_management_access()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync("/api/admin/auth/login", new
        {
            username = "qa-admin",
            password = "TestOnly-Admin-Password-2026!"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var adminResponse = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task Cors_allows_configured_origin_and_rejects_other_origins()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        allowed.Headers.Add("Origin", "https://client.example.test");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.True(allowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigins),
            $"Preflight returned {(int)allowedResponse.StatusCode}: {await allowedResponse.Content.ReadAsStringAsync()}; headers: {string.Join(",", allowedResponse.Headers.Select(h => h.Key))}");
        Assert.Equal("https://client.example.test", allowOrigins!.Single());
        Assert.Contains("Access-Control-Allow-Credentials", allowedResponse.Headers.Select(h => h.Key));

        using var rejected = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        rejected.Headers.Add("Origin", "https://attacker.example");
        rejected.Headers.Add("Access-Control-Request-Method", "POST");
        using var rejectedResponse = await client.SendAsync(rejected);
        Assert.False(rejectedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Admin_session_version_change_invalidates_existing_cookie()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/admin/auth/login", new
        {
            username = "qa-admin",
            password = "TestOnly-Admin-Password-2026!"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        factory.SetAdminSessionVersion("2");
        using var response = await client.GetAsync("/api/admin/users");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Comment_is_saved_and_returned_in_the_feed()
    {
        using var factory = new NewsFeedApplicationFactory();
        using var memberClient = factory.CreateClient();
        using var register = await memberClient.PostAsJsonAsync("/api/auth/register", new
        {
            displayName = "Comment Author",
            username = $"commenter_{Guid.NewGuid():N}",
            password = "StrongTestPassword123!"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        using var createPost = await memberClient.PostAsJsonAsync("/api/posts", new
        {
            sinhalaTitle = "Test post",
            sinhalaSummary = "Integration test summary",
            tag = "#Community",
            link = "https://example.test/story"
        });
        Assert.Equal(HttpStatusCode.Created, createPost.StatusCode);
        using var postJson = JsonDocument.Parse(await createPost.Content.ReadAsStringAsync());
        var postId = postJson.RootElement.GetProperty("id").GetInt32();

        using var adminClient = factory.CreateClient();
        using var adminLogin = await adminClient.PostAsJsonAsync("/api/admin/auth/login", new
        {
            username = "qa-admin",
            password = "TestOnly-Admin-Password-2026!"
        });
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        using var approve = await adminClient.PatchAsync($"/api/admin/posts/{postId}/approve", content: null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        using var comment = await memberClient.PostAsJsonAsync($"/api/posts/{postId}/comments", new { content = "Saved comment" });
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        using var feed = await memberClient.GetAsync("/api/feed");
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        using var feedJson = JsonDocument.Parse(await feed.Content.ReadAsStringAsync());
        var matchingPost = feedJson.RootElement.GetProperty("items").EnumerateArray()
            .Single(post => post.GetProperty("id").GetInt32() == postId);
        Assert.Equal("Saved comment", matchingPost.GetProperty("comments")[0].GetProperty("content").GetString());
    }
}
