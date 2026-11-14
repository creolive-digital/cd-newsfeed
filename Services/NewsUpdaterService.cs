using System.ServiceModel.Syndication;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using cd_newsfeed.Data;
using cd_newsfeed.Models;

namespace cd_newsfeed.Services;

public class NewsUpdaterService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NewsUpdaterService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _stateLock = new();
    private bool _enabled = true;
    private bool _isRunning;
    private DateTimeOffset? _lastRunAt;
    private int? _lastInsertedCount;
    private string? _lastError;

    public NewsSyncStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new NewsSyncStatus(_enabled, _isRunning, _lastRunAt, _lastInsertedCount, _lastError);
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedState = await db.NewsFetcherStates.FindAsync([1], cancellationToken);
        if (persistedState == null)
        {
            persistedState = new NewsFetcherState { Id = 1 };
            db.NewsFetcherStates.Add(persistedState);
        }
        persistedState.IsEnabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        lock (_stateLock) _enabled = enabled;
        _logger.LogInformation("Automatic news fetching {State} by an administrator.", enabled ? "enabled" : "paused");
    }

    public bool QueueRefresh()
    {
        if (!_runLock.Wait(0)) return false;
        _ = RunUpdateAndReleaseAsync(CancellationToken.None);
        return true;
    }

    public static readonly (string Name, string FeedUrl, string Tag)[] DefaultSources =
    [
        ("TechCrunch AI", "https://techcrunch.com/category/artificial-intelligence/feed/", "#AI"),
        ("The Verge", "https://www.theverge.com/rss/index.xml", "#Tech"),
        ("Ars Technica", "https://feeds.arstechnica.com/arstechnica/index", "#Tech"),
        ("MIT Technology Review", "https://www.technologyreview.com/feed/", "#AI"),
        ("WIRED", "https://www.wired.com/feed/rss", "#Tech"),
        ("Google News · AI & Tech", "https://news.google.com/rss/search?q=artificial+intelligence+technology&hl=en-US&gl=US&ceid=US:en", "#AI")
    ];

    public NewsUpdaterService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<NewsUpdaterService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NewsFeedTestResult> TestFeedAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        if (!await IsSafePublicFeedUrlAsync(feedUrl, cancellationToken))
            return new NewsFeedTestResult(false, 0, null, "Use a public HTTPS RSS or Atom feed URL.");

        try
        {
            var items = await FetchItemsFromFeedAsync("Feed test", feedUrl, "#Tech", cancellationToken, includeArticlePageImages: false);
            return items.Count == 0
                ? new NewsFeedTestResult(false, 0, null, "The URL responded, but no feed stories were found.")
                : new NewsFeedTestResult(true, items.Count, items[0].Title, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Administrator feed test failed for {FeedUrl}.", feedUrl);
            return new NewsFeedTestResult(false, 0, null, "Could not read that feed. Check the URL and try again.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NewsUpdaterService starting...");

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persistedState = await db.NewsFetcherStates.FindAsync([1], stoppingToken);
            if (persistedState != null)
            {
                lock (_stateLock) _enabled = persistedState.IsEnabled;
            }
        }

        try
        {
            if (GetStatus().Enabled) await RunUpdateAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error occurred during initial news feed update.");
        }

        // Retry RSS updates regularly so temporary model outages do not hold news back for an hour.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (GetStatus().Enabled) await RunUpdateAsync(stoppingToken);
        }
    }

    private async Task<bool> RunUpdateAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken)) return false;
        await RunUpdateAndReleaseAsync(cancellationToken);
        return true;
    }

    private async Task RunUpdateAndReleaseAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _isRunning = true;
            _lastError = null;
        }

        try
        {
            var insertedCount = await RunFeedUpdateAsync(cancellationToken);
            lock (_stateLock)
            {
                _lastRunAt = DateTimeOffset.UtcNow;
                _lastInsertedCount = insertedCount;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "News feed update failed.");
            lock (_stateLock)
            {
                _lastRunAt = DateTimeOffset.UtcNow;
                _lastError = "News fetching failed. Check the application logs for details.";
            }
        }
        finally
        {
            lock (_stateLock) _isRunning = false;
            _runLock.Release();
        }
    }

    private IReadOnlyList<string> GetGeminiApiKeys()
    {
        var keys = _configuration.GetSection("Gemini:ApiKeys")
            .GetChildren()
            .Select(section => section.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToList();

        var singleKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? _configuration["GEMINI_API_KEY"]
            ?? _configuration["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(singleKey))
        {
            keys.Add(singleKey);
        }

        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task<int> RunFeedUpdateAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing"))
        {
            _logger.LogInformation("News fetching is disabled in the Testing environment.");
            return 0;
        }

        var apiKeys = GetGeminiApiKeys();
        if (apiKeys.Count == 0)
        {
            _logger.LogInformation("News check: no Gemini API keys are configured. Skipping feed translation.");
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var insertedCount = await FetchAndTranslateLatestFeedsAsync(scope.ServiceProvider, apiKeys, maxItemsToInsert: 6, stoppingToken);
        _logger.LogInformation("News feed update finished. Inserted {InsertedCount} translated stories.", insertedCount);
        return insertedCount;
    }

    private async Task<int> FetchAndTranslateLatestFeedsAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<string> apiKeys,
        int maxItemsToInsert,
        CancellationToken stoppingToken)
    {
        var db = serviceProvider.GetRequiredService<AppDbContext>();
        var translator = serviceProvider.GetRequiredService<GeminiTranslatorService>();
        var enabledSources = await db.NewsSources.AsNoTracking()
            .Where(source => source.IsEnabled)
            .OrderBy(source => source.Name)
            .ToListAsync(stoppingToken);

        var rawCandidates = new List<RawFeedCandidate>();

        foreach (var source in enabledSources)
        {
            try
            {
                var items = await FetchItemsFromFeedAsync(source.Name, source.FeedUrl, source.Tag, stoppingToken, includeArticlePageImages: true);
                rawCandidates.AddRange(items);
                await UpdateSourceStatusAsync(db, source.Id, items.Count, null, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch RSS feed from {SourceName} ({FeedUrl})", source.Name, source.FeedUrl);
                await UpdateSourceStatusAsync(db, source.Id, 0, "Could not fetch this feed. Check its URL and availability.", stoppingToken);
            }
        }

        // Sort candidates by PublishDate descending
        var sortedCandidates = rawCandidates
            .OrderByDescending(c => c.PublishDate)
            .ToList();

        int insertedCount = 0;
        int duplicateCount = 0;
        int translationFailureCount = 0;

        foreach (var candidate in sortedCandidates)
        {
            // Check if link already exists in DB to prevent duplicates
            var existingPost = await db.Posts.FirstOrDefaultAsync(
                p => (p.Link != null && candidate.Link != null && p.Link == candidate.Link) ||
                     p.SinhalaTitle == candidate.Title,
                stoppingToken);

            if (existingPost != null)
            {
                duplicateCount++;
                if (string.IsNullOrWhiteSpace(existingPost.ImageUrl) && !string.IsNullOrWhiteSpace(candidate.ImageUrl))
                {
                    existingPost.ImageUrl = candidate.ImageUrl;
                    await db.SaveChangesAsync(stoppingToken);
                }
                continue;
            }

            // Keep scanning for existing items so missing images can be backfilled,
            // while still limiting how many newly translated stories enter the feed.
            if (insertedCount >= maxItemsToInsert)
            {
                continue;
            }

            _logger.LogInformation("Translating article: {Title} from {Source}", candidate.Title, candidate.SourceName);
            var translated = await translator.TranslateNewsAsync(candidate.Title, candidate.Summary, apiKeys, stoppingToken);

            if (translated != null &&
                !string.IsNullOrWhiteSpace(translated.SinhalaTitle) &&
                !string.IsNullOrWhiteSpace(translated.SinhalaSummary))
            {
                var newPost = new Post
                {
                    SinhalaTitle = translated.SinhalaTitle,
                    SinhalaSummary = translated.SinhalaSummary,
                    SinhalaContent = translated.SinhalaContent ?? translated.SinhalaSummary,
                    ImageUrl = candidate.ImageUrl,
                    Source = candidate.SourceName,
                    Link = candidate.Link,
                    Tag = candidate.Tag,
                    IsAutoNews = true,
                    Status = "Approved",
                    AuthorId = null,
                    CreatedAt = candidate.PublishDate == DateTimeOffset.MinValue
                        ? DateTime.UtcNow
                        : candidate.PublishDate.UtcDateTime
                };

                db.Posts.Add(newPost);
                await db.SaveChangesAsync(stoppingToken);
                insertedCount++;
                _logger.LogInformation("Inserted translated auto-news post: {Id} - {Title}", newPost.Id, newPost.SinhalaTitle);
            }
            else
            {
                translationFailureCount++;
            }
        }

        _logger.LogInformation("News scan found {CandidateCount} RSS stories; {DuplicateCount} were already in the database and {TranslationFailureCount} could not be translated.",
            sortedCandidates.Count, duplicateCount, translationFailureCount);
        return insertedCount;
    }

    private static async Task UpdateSourceStatusAsync(AppDbContext db, int sourceId, int itemCount, string? error, CancellationToken cancellationToken)
    {
        var source = await db.NewsSources.FirstOrDefaultAsync(item => item.Id == sourceId, cancellationToken);
        if (source == null) return;
        source.LastCheckedAt = DateTime.UtcNow;
        source.LastItemCount = itemCount;
        source.LastError = error;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<RawFeedCandidate>> FetchItemsFromFeedAsync(
        string sourceName,
        string feedUrl,
        string defaultTag,
        CancellationToken stoppingToken,
        bool includeArticlePageImages)
    {
        if (!await IsSafePublicFeedUrlAsync(feedUrl, stoppingToken))
            throw new InvalidOperationException("Only public HTTPS feed URLs can be fetched.");

        var list = new List<RawFeedCandidate>();
        var httpClient = _httpClientFactory.CreateClient("RssClient");

        using var response = await GetSafeResponseAsync(httpClient, feedUrl, stoppingToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
        using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true
        });

        var feed = SyndicationFeed.Load(xmlReader);
        if (feed == null)
        {
            return list;
        }

        foreach (var item in feed.Items.Take(20))
        {
            var title = item.Title?.Text ?? string.Empty;
            var rawSummary = item.Summary?.Text ?? string.Empty;
            var rawContent = rawSummary;

            if (item.Content is TextSyndicationContent textContent)
            {
                rawContent += " " + textContent.Text;
                if (string.IsNullOrWhiteSpace(rawSummary)) rawSummary = textContent.Text;
            }
            else if (item.Content is XmlSyndicationContent xmlContent)
            {
                try
                {
                    using var contentReader = xmlContent.GetReaderAtContent();
                    var contentElement = XElement.Load(contentReader);
                    rawContent += " " + contentElement;
                    if (string.IsNullOrWhiteSpace(rawSummary)) rawSummary = contentElement.Value;
                }
                catch (XmlException) { }
            }

            var cleanSummary = StripHtml(rawSummary);
            var link = item.Links.FirstOrDefault()?.Uri?.ToString();
            var publishDate = item.PublishDate != DateTimeOffset.MinValue ? item.PublishDate : item.LastUpdatedTime;

            // Feeds commonly put thumbnails in media extensions or encoded HTML rather than enclosures.
            var imageUrl = ResolveFeedImageUrl(ExtractImageUrl(item, WebUtility.HtmlDecode(rawContent)), link ?? feedUrl, feedUrl);

            if (!string.IsNullOrWhiteSpace(title))
            {
                list.Add(new RawFeedCandidate(sourceName, title.Trim(), cleanSummary, link, imageUrl, defaultTag, publishDate));
            }
        }

        if (includeArticlePageImages && list.Any(candidate => string.IsNullOrWhiteSpace(candidate.ImageUrl) && !string.IsNullOrWhiteSpace(candidate.Link)))
        {
            using var imageFetchLimit = new SemaphoreSlim(4, 4);
            var enrichTasks = list.Select(async candidate =>
            {
                if (!string.IsNullOrWhiteSpace(candidate.ImageUrl) || string.IsNullOrWhiteSpace(candidate.Link)) return candidate;
                await imageFetchLimit.WaitAsync(stoppingToken);
                try
                {
                    var pageImage = await FetchArticleImageUrlAsync(httpClient, candidate.Link, stoppingToken);
                    return string.IsNullOrWhiteSpace(pageImage) ? candidate : candidate with { ImageUrl = pageImage };
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    return candidate;
                }
                finally
                {
                    imageFetchLimit.Release();
                }
            });
            list = (await Task.WhenAll(enrichTasks)).ToList();
        }

        return list;
    }

    private static async Task<HttpResponseMessage> GetSafeResponseAsync(HttpClient httpClient, string feedUrl, CancellationToken cancellationToken)
    {
        var currentUri = new Uri(feedUrl, UriKind.Absolute);
        for (var redirectCount = 0; redirectCount <= 4; redirectCount++)
        {
            if (!await IsSafePublicFeedUrlAsync(currentUri.AbsoluteUri, cancellationToken))
                throw new InvalidOperationException("The feed redirected to a non-public URL.");

            var response = await httpClient.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is < 300 or >= 400) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location == null || redirectCount == 4)
                throw new HttpRequestException("The feed redirected too many times or had no redirect location.");
            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new HttpRequestException("The feed redirected too many times.");
    }

    private static async Task<bool> IsSafePublicFeedUrlAsync(string feedUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.IsLoopback ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return false;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literalAddress))
        {
            addresses = [literalAddress];
        }
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
            catch (SocketException) { return false; }
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast) return false;
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xfe) != 0xfc && !(bytes[0] == 0xfe && (bytes[1] & 0xc0) != 0x00);

        var first = bytes[0];
        var second = bytes[1];
        return first != 0 && first != 10 && first != 127 && first < 224 &&
            !(first == 169 && second == 254) &&
            !(first == 172 && second >= 16 && second <= 31) &&
            !(first == 192 && second == 168) &&
            !(first == 100 && second >= 64 && second <= 127);
    }

    private static string? ExtractImageUrl(SyndicationItem item, string rawContent)
    {
        // 1. Check enclosure links
        var enclosure = item.Links.FirstOrDefault(l =>
            (l.RelationshipType?.Equals("enclosure", StringComparison.OrdinalIgnoreCase) == true ||
             l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) &&
            (l.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
             l.Uri.ToString().EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
             l.Uri.ToString().EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
             l.Uri.ToString().EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
             l.Uri.ToString().EndsWith(".webp", StringComparison.OrdinalIgnoreCase)));

        if (enclosure?.Uri != null)
        {
            return enclosure.Uri.ToString();
        }

        // 2. Check XML element extensions (e.g., Media RSS)
        foreach (var ext in item.ElementExtensions)
        {
            if (ext.OuterName.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                ext.OuterName.Equals("thumbnail", StringComparison.OrdinalIgnoreCase) ||
                ext.OuterName.Equals("group", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = ext.GetReader();
                    var extension = XElement.Load(reader);
                    var imageElement = extension.DescendantsAndSelf().FirstOrDefault(element =>
                        (element.Name.LocalName.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                         element.Name.LocalName.Equals("thumbnail", StringComparison.OrdinalIgnoreCase)) &&
                        (element.Attribute("url") != null || element.Attribute("href") != null));
                    var url = imageElement?.Attribute("url")?.Value ?? imageElement?.Attribute("href")?.Value;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
                catch { }
            }
        }

        // 3. Fallback: Search <img> tag in HTML
        if (!string.IsNullOrWhiteSpace(rawContent))
        {
            foreach (Match match in Regex.Matches(rawContent, @"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                foreach (var attribute in new[] { "src", "data-src", "data-original", "data-lazy-src" })
                {
                    var value = GetHtmlAttribute(match.Value, attribute);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }

                var srcSet = GetHtmlAttribute(match.Value, "srcset");
                var firstSrcSetUrl = srcSet?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstSrcSetUrl)) return firstSrcSetUrl;
            }
        }

        return null;
    }

    private async Task<string?> FetchArticleImageUrlAsync(HttpClient httpClient, string articleUrl, CancellationToken cancellationToken)
    {
        if (!await IsSafePublicFeedUrlAsync(articleUrl, cancellationToken)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await GetSafeResponseAsync(httpClient, articleUrl, timeout.Token);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > 2_000_000 ||
                (response.Content.Headers.ContentType?.MediaType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true &&
                 response.Content.Headers.ContentType?.MediaType?.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase) != true))
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var htmlBuffer = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (htmlBuffer.Length <= 2_000_000)
            {
                var remaining = (int)Math.Min(buffer.Length, 2_000_001 - htmlBuffer.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), timeout.Token);
                if (read == 0) break;
                htmlBuffer.Write(buffer, 0, read);
            }
            if (htmlBuffer.Length > 2_000_000) return null;

            var html = Encoding.UTF8.GetString(htmlBuffer.ToArray());
            return ResolveFeedImageUrl(ExtractPageImageUrl(html), articleUrl, articleUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            _logger.LogDebug("Could not retrieve article image metadata from {ArticleUrl}: {Message}", articleUrl, ex.Message);
            return null;
        }
    }

    private static string? ExtractPageImageUrl(string html)
    {
        foreach (Match match in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var tag = match.Value;
            var property = GetHtmlAttribute(tag, "property") ?? GetHtmlAttribute(tag, "name") ?? GetHtmlAttribute(tag, "itemprop");
            if (property?.Equals("og:image", StringComparison.OrdinalIgnoreCase) == true ||
                property?.Equals("og:image:url", StringComparison.OrdinalIgnoreCase) == true ||
                property?.Equals("twitter:image", StringComparison.OrdinalIgnoreCase) == true ||
                property?.Equals("twitter:image:src", StringComparison.OrdinalIgnoreCase) == true ||
                property?.Equals("image", StringComparison.OrdinalIgnoreCase) == true)
            {
                var imageUrl = GetHtmlAttribute(tag, "content");
                if (!string.IsNullOrWhiteSpace(imageUrl)) return imageUrl;
            }
        }

        foreach (Match match in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var tag = match.Value;
            if (GetHtmlAttribute(tag, "rel")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => value.Equals("image_src", StringComparison.OrdinalIgnoreCase)) == true)
            {
                var imageUrl = GetHtmlAttribute(tag, "href");
                if (!string.IsNullOrWhiteSpace(imageUrl)) return imageUrl;
            }
        }

        return null;
    }

    private static string? GetHtmlAttribute(string tag, string attributeName)
    {
        var pattern = $@"(?:^|\s){Regex.Escape(attributeName)}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))";
        var match = Regex.Match(tag, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value :
            match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        return WebUtility.HtmlDecode(value.Trim());
    }

    private static string? ResolveFeedImageUrl(string? imageUrl, string itemLink, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;
        if (!Uri.TryCreate(itemLink, UriKind.Absolute, out var baseUri) && !Uri.TryCreate(feedUrl, UriKind.Absolute, out baseUri))
            return null;
        if (!Uri.TryCreate(baseUri, imageUrl.Trim(), out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttps && resolved.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(resolved.UserInfo))
            return null;
        return resolved.AbsoluteUri;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var stripped = Regex.Replace(input, "<.*?>", string.Empty);
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private record RawFeedCandidate(
        string SourceName,
        string Title,
        string Summary,
        string? Link,
        string? ImageUrl,
        string Tag,
        DateTimeOffset PublishDate
    );
}

public sealed record NewsSyncStatus(
    bool Enabled,
    bool IsRunning,
    DateTimeOffset? LastRunAt,
    int? LastInsertedCount,
    string? LastError);

public sealed record NewsFeedTestResult(bool IsValid, int ItemCount, string? LatestTitle, string? Error);
