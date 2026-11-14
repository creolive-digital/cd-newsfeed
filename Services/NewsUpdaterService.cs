using System.ServiceModel.Syndication;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    private readonly GeminiImageGenerator _imageGenerator;
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
        ("Google News · AI & Tech", "https://news.google.com/rss/search?q=artificial+intelligence+technology&hl=en-US&gl=US&ceid=US:en", "#AI"),
        ("OpenAI News · Website", "https://openai.com/news/", "#AI"),
        ("Anthropic Newsroom · Website", "https://www.anthropic.com/news", "#AI"),
        ("Google AI Blog · Website", "https://blog.google/innovation-and-ai/technology/ai/", "#AI"),
        ("Hacker News · Website", "https://news.ycombinator.com/", "#Tech")
    ];

    public NewsUpdaterService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        GeminiImageGenerator imageGenerator,
        ILogger<NewsUpdaterService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _imageGenerator = imageGenerator;
        _logger = logger;
    }

    public async Task<NewsFeedTestResult> TestFeedAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        if (!await IsSafePublicFeedUrlAsync(feedUrl, cancellationToken))
            return new NewsFeedTestResult(false, 0, null, "Use a public HTTPS RSS/Atom feed or a readable news website page.");

        try
        {
            var items = await FetchItemsFromFeedAsync("Source test", feedUrl, "#Tech", cancellationToken, includeArticlePageImages: false);
            return items.Count == 0
                ? new NewsFeedTestResult(false, 0, null, "The URL responded, but no feed stories were found.")
                : new NewsFeedTestResult(true, items.Count, items[0].Title, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Administrator feed test failed for {FeedUrl}.", feedUrl);
            return new NewsFeedTestResult(false, 0, null, "Could not read that source. Check the URL and make sure it exposes a public RSS feed or article links.");
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

        var freshAfter = DateTimeOffset.UtcNow.AddDays(-30);
        var staleCandidateCount = rawCandidates.Count(candidate => candidate.PublishDate < freshAfter);

        // Keep outdated stories out of the active feed and avoid spending translation quota on them.
        var sortedCandidates = rawCandidates
            .Where(candidate => candidate.PublishDate >= freshAfter)
            .OrderByDescending(c => c.PublishDate)
            .ToList();

        int insertedCount = 0;
        int duplicateCount = 0;
        int translationFailureCount = 0;
        int generatedImageCount = 0;
        int remoteImageAttempts = 0;
        var attemptedImageGeneration = new HashSet<int>();
        const int maxGeneratedImagesPerRun = 3;
        const int maxRemoteImageAttemptsPerRun = 12;
        var seenCandidateTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownTranslatedTitles = (await db.Posts.AsNoTracking()
                .Where(post => post.IsAutoNews && post.Status == "Approved")
                .Select(post => post.SinhalaTitle)
                .ToListAsync(stoppingToken))
            .Select(NormalizeStoryTitle)
            .Where(title => title.Length >= 16)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingArticleLinks = await db.Posts.AsNoTracking()
            .Where(post => post.IsAutoNews && post.Link != null)
            .Select(post => new { post.Id, post.Link })
            .ToListAsync(stoppingToken);
        var knownStoryIdsByUrl = existingArticleLinks
            .Select(item => (Key: NormalizeStoryUrl(item.Link), item.Id))
            .Where(item => item.Key != null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in sortedCandidates)
        {
            var normalizedTitle = NormalizeStoryTitle(candidate.Title);
            if (normalizedTitle.Length >= 16 && !seenCandidateTitles.Add(normalizedTitle))
            {
                duplicateCount++;
                continue;
            }

            var normalizedUrl = NormalizeStoryUrl(candidate.Link);
            Post? existingPost = null;
            if (normalizedUrl != null && knownStoryIdsByUrl.TryGetValue(normalizedUrl, out var existingId))
                existingPost = await db.Posts.FindAsync([existingId], stoppingToken);

            if (existingPost != null)
            {
                duplicateCount++;
                var imageChanged = false;
                var publisherImageFailed = false;
                if (string.IsNullOrWhiteSpace(existingPost.ImageUrl) && !string.IsNullOrWhiteSpace(candidate.ImageUrl))
                {
                    existingPost.ImageUrl = candidate.ImageUrl;
                    imageChanged = true;
                }

                if (IsRemoteImageUrl(existingPost.ImageUrl) && remoteImageAttempts < maxRemoteImageAttemptsPerRun)
                {
                    remoteImageAttempts++;
                    var cachedImage = await CacheArticleImageAsync(existingPost.ImageUrl!, stoppingToken);
                    if (!string.IsNullOrWhiteSpace(cachedImage))
                    {
                        existingPost.ImageUrl = cachedImage;
                        imageChanged = true;
                    }
                    else
                    {
                        publisherImageFailed = true;
                    }
                }

                if (string.IsNullOrWhiteSpace(existingPost.ImageUrl) || publisherImageFailed)
                {
                    if (generatedImageCount < maxGeneratedImagesPerRun)
                    {
                        attemptedImageGeneration.Add(existingPost.Id);
                        var generatedImage = await _imageGenerator.GenerateAndSaveAsync(
                            candidate.Title, candidate.Summary, apiKeys, stoppingToken);
                        if (!string.IsNullOrWhiteSpace(generatedImage))
                        {
                            existingPost.ImageUrl = generatedImage;
                            imageChanged = true;
                            generatedImageCount++;
                        }
                    }
                }
                if (imageChanged) await db.SaveChangesAsync(stoppingToken);
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
                var translatedTitleKey = NormalizeStoryTitle(translated.SinhalaTitle);
                if (translatedTitleKey.Length >= 16 && !knownTranslatedTitles.Add(translatedTitleKey))
                {
                    duplicateCount++;
                    continue;
                }

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
                if (normalizedUrl != null) knownStoryIdsByUrl[normalizedUrl] = newPost.Id;
                if (IsRemoteImageUrl(newPost.ImageUrl))
                {
                    var cachedImage = await CacheArticleImageAsync(newPost.ImageUrl!, stoppingToken);
                    if (!string.IsNullOrWhiteSpace(cachedImage))
                    {
                        newPost.ImageUrl = cachedImage;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    else
                    {
                        newPost.ImageUrl = null;
                    }
                }
                if (string.IsNullOrWhiteSpace(newPost.ImageUrl) && generatedImageCount < maxGeneratedImagesPerRun)
                {
                    attemptedImageGeneration.Add(newPost.Id);
                    newPost.ImageUrl = await _imageGenerator.GenerateAndSaveAsync(
                        candidate.Title, candidate.Summary, apiKeys, stoppingToken);
                    if (!string.IsNullOrWhiteSpace(newPost.ImageUrl))
                    {
                        await db.SaveChangesAsync(stoppingToken);
                        generatedImageCount++;
                    }
                }
                insertedCount++;
                _logger.LogInformation("Inserted translated auto-news post: {Id} - {Title}; generated image: {HasGeneratedImage}",
                    newPost.Id, newPost.SinhalaTitle, !string.IsNullOrWhiteSpace(newPost.ImageUrl) && newPost.ImageUrl.StartsWith("/uploads/articles/generated/", StringComparison.Ordinal));
            }
            else
            {
                translationFailureCount++;
            }
        }

        // Work through stored imports too, including articles that have fallen out
        // of their publisher's latest RSS page since they were first imported.
        var imageBackfillPosts = await db.Posts
            .Where(post => post.IsAutoNews && post.Status == "Approved" &&
                           (post.ImageUrl == null || post.ImageUrl == "" ||
                            post.ImageUrl.StartsWith("http://") || post.ImageUrl.StartsWith("https://")))
            .OrderByDescending(post => post.CreatedAt)
            .Take(12)
            .ToListAsync(stoppingToken);
        foreach (var post in imageBackfillPosts)
        {
            if (attemptedImageGeneration.Contains(post.Id)) continue;
            if (IsRemoteImageUrl(post.ImageUrl) && remoteImageAttempts < maxRemoteImageAttemptsPerRun)
            {
                remoteImageAttempts++;
                var cachedImage = await CacheArticleImageAsync(post.ImageUrl!, stoppingToken);
                if (!string.IsNullOrWhiteSpace(cachedImage))
                {
                    post.ImageUrl = cachedImage;
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }
            }

            if (generatedImageCount >= maxGeneratedImagesPerRun) break;
            attemptedImageGeneration.Add(post.Id);
            var generatedImage = await _imageGenerator.GenerateAndSaveAsync(
                post.SinhalaTitle, post.SinhalaSummary, apiKeys, stoppingToken);
            if (!string.IsNullOrWhiteSpace(generatedImage))
            {
                post.ImageUrl = generatedImage;
                await db.SaveChangesAsync(stoppingToken);
                generatedImageCount++;
            }
        }

        _logger.LogInformation("News scan found {CandidateCount} RSS stories; {DuplicateCount} were already in the database and {TranslationFailureCount} could not be translated.",
            sortedCandidates.Count, duplicateCount, translationFailureCount);
        _logger.LogInformation("Skipped {StaleCandidateCount} stories older than the 30-day freshness window.", staleCandidateCount);
        _logger.LogInformation("Generated {GeneratedImageCount} missing article images (limit {MaxGeneratedImages}).",
            generatedImageCount, maxGeneratedImagesPerRun);
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

    private static bool IsRemoteImageUrl(string? imageUrl) =>
        Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string NormalizeStoryTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var normalized = title.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }

    private static string? NormalizeStoryUrl(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        builder.Path = builder.Path.TrimEnd('/');
        if (builder.Path.Length == 0) builder.Path = "/";
        var ignoredTrackingParameters = new[] { "fbclid", "gclid", "mc_cid", "mc_eid", "ref", "source" };
        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var separator = part.IndexOf('=');
                var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
                return !key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) &&
                       !ignoredTrackingParameters.Contains(key, StringComparer.OrdinalIgnoreCase);
            })
            .OrderBy(part => part, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        builder.Query = string.Join("&", queryParts);
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private async Task<string?> CacheArticleImageAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (!await IsSafePublicFeedUrlAsync(imageUrl, cancellationToken)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var httpClient = _httpClientFactory.CreateClient("RssClient");
            using var response = await GetSafeResponseAsync(httpClient, imageUrl, timeout.Token);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > 8_000_000 ||
                mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                return null;

            var extension = mediaType.ToLowerInvariant() switch
            {
                "image/jpeg" => "jpg",
                "image/png" => "png",
                "image/webp" => "webp",
                "image/gif" => "gif",
                "image/avif" => "avif",
                _ => null
            };
            if (extension == null) return null;

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (output.Length <= 8_000_000)
            {
                var remaining = (int)Math.Min(buffer.Length, 8_000_001 - output.Length);
                var read = await input.ReadAsync(buffer.AsMemory(0, remaining), timeout.Token);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            if (output.Length is 0 or > 8_000_000) return null;

            var directory = Path.Combine(_environment.ContentRootPath, "wwwroot", "uploads", "articles", "publishers");
            Directory.CreateDirectory(directory);
            var fileName = $"{Guid.NewGuid():N}.{extension}";
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), output.ToArray(), timeout.Token);
            return $"/uploads/articles/publishers/{fileName}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            _logger.LogDebug("Could not cache publisher image {ImageUrl}: {Message}", imageUrl, ex.Message);
            return null;
        }
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

        var payload = await ReadBoundedContentAsync(response.Content, 5_000_000, stoppingToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true ||
            mediaType?.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await FetchItemsFromWebsiteAsync(sourceName, feedUrl, defaultTag, Encoding.UTF8.GetString(payload), includeArticlePageImages, stoppingToken);
        }

        SyndicationFeed? feed;
        try
        {
            using var payloadStream = new MemoryStream(payload);
            using var xmlReader = XmlReader.Create(payloadStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true
            });
            feed = SyndicationFeed.Load(xmlReader);
        }
        catch (XmlException) when (LooksLikeHtml(payload))
        {
            return await FetchItemsFromWebsiteAsync(sourceName, feedUrl, defaultTag, Encoding.UTF8.GetString(payload), includeArticlePageImages, stoppingToken);
        }
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
            if (publishDate == DateTimeOffset.MinValue) publishDate = DateTimeOffset.UtcNow;

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

    private async Task<List<RawFeedCandidate>> FetchItemsFromWebsiteAsync(
        string sourceName,
        string sourceUrl,
        string defaultTag,
        string html,
        bool fetchSameSiteArticlePages,
        CancellationToken cancellationToken)
    {
        var results = new List<RawFeedCandidate>();
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri)) return results;

        results.AddRange(ExtractJsonLdArticles(html, sourceName, sourceUrl, defaultTag));
        var pageMetadata = ExtractHtmlMetadata(html, sourceUrl);
        if (results.Count == 0 && pageMetadata.PublishDate.HasValue &&
            !string.IsNullOrWhiteSpace(pageMetadata.Title) &&
            !string.IsNullOrWhiteSpace(pageMetadata.Summary))
        {
            results.Add(new RawFeedCandidate(
                sourceName, pageMetadata.Title, pageMetadata.Summary, sourceUrl, pageMetadata.ImageUrl,
                defaultTag, pageMetadata.PublishDate ?? DateTimeOffset.MinValue));
        }

        var knownLinks = results.Select(item => item.Link).Where(link => link != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pageLinks = ExtractArticleLinks(html, sourceUri)
            .Where(item => !knownLinks.Contains(item.Url))
            .Take(10)
            .ToList();

        using var pageLimit = new SemaphoreSlim(3, 3);
        var tasks = pageLinks.Select(async item =>
        {
            await pageLimit.WaitAsync(cancellationToken);
            try
            {
                if (fetchSameSiteArticlePages &&
                    Uri.TryCreate(item.Url, UriKind.Absolute, out var articleUri) &&
                    articleUri.Host.Equals(sourceUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    var page = await FetchArticleMetadataAsync(item.Url, cancellationToken);
                    if (page != null && !string.IsNullOrWhiteSpace(page.Value.Title))
                    {
                        return new RawFeedCandidate(
                            sourceName,
                            page.Value.Title,
                            string.IsNullOrWhiteSpace(page.Value.Summary) ? item.Title : page.Value.Summary,
                            item.Url,
                            page.Value.ImageUrl,
                            defaultTag,
                            page.Value.PublishDate ?? DateTimeOffset.UtcNow);
                    }
                }

                return new RawFeedCandidate(sourceName, item.Title, item.Title, item.Url, null, defaultTag, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                pageLimit.Release();
            }
        });

        foreach (var item in await Task.WhenAll(tasks))
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.Title))
                results.Add(item);
        }

        return results
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .DistinctBy(item => item.Link ?? item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private async Task<(string Title, string Summary, string? ImageUrl, DateTimeOffset? PublishDate)?> FetchArticleMetadataAsync(
        string articleUrl,
        CancellationToken cancellationToken)
    {
        if (!await IsSafePublicFeedUrlAsync(articleUrl, cancellationToken)) return null;
        var client = _httpClientFactory.CreateClient("RssClient");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await GetSafeResponseAsync(client, articleUrl, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await ReadBoundedContentAsync(response.Content, 2_000_000, timeout.Token);
            var html = Encoding.UTF8.GetString(bytes);
            var structured = ExtractJsonLdArticles(html, string.Empty, articleUrl, "#Tech").FirstOrDefault();
            var metadata = ExtractHtmlMetadata(html, articleUrl);
            var title = structured?.Title ?? metadata.Title;
            var summary = structured?.Summary ?? metadata.Summary;
            var imageUrl = structured?.ImageUrl ?? metadata.ImageUrl;
            var published = structured?.PublishDate ?? metadata.PublishDate;
            if (string.IsNullOrWhiteSpace(title)) return null;
            return (title, summary ?? string.Empty, imageUrl, published);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or XmlException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            _logger.LogDebug("Could not read non-RSS article metadata from {ArticleUrl}: {Message}", articleUrl, ex.Message);
            return null;
        }
    }

    private static List<RawFeedCandidate> ExtractJsonLdArticles(string html, string sourceName, string baseUrl, string tag)
    {
        var articles = new List<RawFeedCandidate>();
        foreach (Match match in Regex.Matches(
                     html,
                     "<script\\b(?=[^>]*type\\s*=\\s*['\\\"]application/ld\\+json['\\\"])[^>]*>(.*?)</script>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            try
            {
                using var document = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups[1].Value));
                AddJsonLdArticles(document.RootElement, sourceName, baseUrl, tag, articles);
            }
            catch (JsonException) { }
        }
        return articles;
    }

    private static void AddJsonLdArticles(JsonElement element, string sourceName, string baseUrl, string tag, List<RawFeedCandidate> output)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) AddJsonLdArticles(child, sourceName, baseUrl, tag, output);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        if (element.TryGetProperty("@type", out var articleType) &&
            (articleType.ValueKind == JsonValueKind.Array
                ? articleType.EnumerateArray().Any(value => IsArticleType(value.GetString()))
                : IsArticleType(articleType.GetString())))
        {
            var title = GetJsonLdString(element, "headline") ?? GetJsonLdString(element, "name");
            var summary = GetJsonLdString(element, "description") ?? GetJsonLdString(element, "abstract") ?? string.Empty;
            var link = GetJsonLdString(element, "url") ?? GetJsonLdNestedString(element, "mainEntityOfPage", "@id") ?? baseUrl;
            var image = GetJsonLdString(element, "image") ?? GetJsonLdNestedString(element, "image", "url");
            if (element.TryGetProperty("image", out var imageElement) && imageElement.ValueKind == JsonValueKind.Array)
                image = imageElement.EnumerateArray().Select(value =>
                    value.ValueKind == JsonValueKind.String ? value.GetString() : GetJsonLdString(value, "url"))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            var dateText = GetJsonLdString(element, "datePublished") ?? GetJsonLdString(element, "dateCreated");
            var date = DateTimeOffset.TryParse(dateText, out var parsedDate) ? parsedDate : DateTimeOffset.MinValue;
            if (!string.IsNullOrWhiteSpace(title))
                output.Add(new RawFeedCandidate(sourceName, title.Trim(), StripHtml(summary), link, ResolveFeedImageUrl(image, link, baseUrl), tag, date));
        }

        foreach (var propertyName in new[] { "@graph", "itemListElement", "item", "mainEntity" })
        {
            if (element.TryGetProperty(propertyName, out var child))
                AddJsonLdArticles(child, sourceName, baseUrl, tag, output);
        }
    }

    private static string? GetJsonLdString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (value.ValueKind == JsonValueKind.Object)
            return GetJsonLdString(value, "url") ?? GetJsonLdString(value, "@id");
        return null;
    }

    private static bool IsArticleType(string? type) =>
        type?.Split('/', ':').LastOrDefault()?.Equals("Article", StringComparison.OrdinalIgnoreCase) == true ||
        type?.Split('/', ':').LastOrDefault()?.Equals("NewsArticle", StringComparison.OrdinalIgnoreCase) == true ||
        type?.Split('/', ':').LastOrDefault()?.Equals("BlogPosting", StringComparison.OrdinalIgnoreCase) == true ||
        type?.Split('/', ':').LastOrDefault()?.Equals("ReportageNewsArticle", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetJsonLdNestedString(JsonElement element, string propertyName, string nestedProperty)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? GetJsonLdString(value, nestedProperty)
            : null;
    }

    private static List<(string Url, string Title)> ExtractArticleLinks(string html, Uri sourceUri)
    {
        var candidates = new List<(string Url, string Title)>();
        foreach (Match match in Regex.Matches(html, "<a\\b[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var href = GetHtmlAttribute(match.Value[..(match.Value.IndexOf('>') + 1)], "href");
            var title = StripHtml(WebUtility.HtmlDecode(match.Groups[1].Value));
            if (string.IsNullOrWhiteSpace(href) || title.Length < 18) continue;
            if (!Uri.TryCreate(sourceUri, href, out var link) ||
                (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(link.UserInfo) ||
                string.IsNullOrWhiteSpace(link.AbsolutePath.Trim('/')) ||
                Regex.IsMatch(link.AbsolutePath, @"\\.(?:xml|json|jpg|jpeg|png|gif|webp|svg|css|js)$", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(link.AbsolutePath, @"/(?:about|contact|privacy|terms|search|category|tag|author|login|register)(?:/|$)", RegexOptions.IgnoreCase))
                continue;
            candidates.Add((link.AbsoluteUri, title));
        }
        return candidates.DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string? Title, string? Summary, string? ImageUrl, DateTimeOffset? PublishDate) ExtractHtmlMetadata(string html, string baseUrl)
    {
        string? title = null, summary = null, image = null, published = null;
        foreach (Match match in Regex.Matches(html, "<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var tag = match.Value;
            var key = GetHtmlAttribute(tag, "property") ?? GetHtmlAttribute(tag, "name") ?? GetHtmlAttribute(tag, "itemprop");
            var value = GetHtmlAttribute(tag, "content");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            if (key.Equals("og:title", StringComparison.OrdinalIgnoreCase) || key.Equals("twitter:title", StringComparison.OrdinalIgnoreCase) || key.Equals("headline", StringComparison.OrdinalIgnoreCase)) title ??= value;
            else if (key.Equals("og:description", StringComparison.OrdinalIgnoreCase) || key.Equals("description", StringComparison.OrdinalIgnoreCase) || key.Equals("twitter:description", StringComparison.OrdinalIgnoreCase)) summary ??= value;
            else if (key.Equals("og:image", StringComparison.OrdinalIgnoreCase) || key.Equals("twitter:image", StringComparison.OrdinalIgnoreCase) || key.Equals("image", StringComparison.OrdinalIgnoreCase)) image ??= value;
            else if (key.Equals("article:published_time", StringComparison.OrdinalIgnoreCase) || key.Equals("datePublished", StringComparison.OrdinalIgnoreCase) || key.Equals("publish_date", StringComparison.OrdinalIgnoreCase)) published ??= value;
        }

        if (title == null)
        {
            var titleMatch = Regex.Match(html, "<title\\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (titleMatch.Success) title = StripHtml(WebUtility.HtmlDecode(titleMatch.Groups[1].Value));
        }
        if (title == null)
        {
            var headingMatch = Regex.Match(html, "<h1\\b[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (headingMatch.Success) title = StripHtml(WebUtility.HtmlDecode(headingMatch.Groups[1].Value));
        }
        var publishDate = DateTimeOffset.TryParse(published, out var date) ? date : (DateTimeOffset?)null;
        return (title, summary, ResolveFeedImageUrl(image, baseUrl, baseUrl), publishDate);
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("The source response is too large to process.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (output.Length <= maxBytes)
        {
            var remaining = (int)Math.Min(buffer.Length, maxBytes + 1L - output.Length);
            var read = await input.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (output.Length > maxBytes) throw new InvalidDataException("The source response is too large to process.");
        return output.ToArray();
    }

    private static bool LooksLikeHtml(byte[] payload)
    {
        var prefix = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 500)).TrimStart();
        return prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
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
