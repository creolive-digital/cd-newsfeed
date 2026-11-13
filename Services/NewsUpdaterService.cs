using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using cd_newsfeed.Data;
using cd_newsfeed.Models;

namespace cd_newsfeed.Services;

public class NewsUpdaterService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NewsUpdaterService> _logger;

    private static readonly (string SourceName, string FeedUrl, string DefaultTag)[] FeedSources =
    [
        ("TechCrunch AI", "https://techcrunch.com/category/artificial-intelligence/feed/", "#AI"),
        ("The Verge", "https://www.theverge.com/rss/index.xml", "#Tech")
    ];

    public NewsUpdaterService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<NewsUpdaterService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NewsUpdaterService starting...");

        await RunFeedUpdateAsync(stoppingToken);

        // Hourly loop
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunFeedUpdateAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error occurred during hourly news feed update.");
            }
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

    private async Task RunFeedUpdateAsync(CancellationToken stoppingToken)
    {
        var apiKeys = GetGeminiApiKeys();
        if (apiKeys.Count == 0)
        {
            _logger.LogInformation("News check: no Gemini API keys are configured. Skipping feed translation.");
            return;
        }

        _logger.LogInformation("News check: Fetching latest RSS items from TechCrunch AI and The Verge...");
        using var scope = _scopeFactory.CreateScope();
        await FetchAndTranslateLatestFeedsAsync(scope.ServiceProvider, apiKeys, maxItemsToInsert: 6, stoppingToken);
    }

    private async Task<int> FetchAndTranslateLatestFeedsAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<string> apiKeys,
        int maxItemsToInsert,
        CancellationToken stoppingToken)
    {
        var db = serviceProvider.GetRequiredService<AppDbContext>();
        var translator = serviceProvider.GetRequiredService<GeminiTranslatorService>();

        var rawCandidates = new List<RawFeedCandidate>();

        foreach (var (sourceName, feedUrl, tag) in FeedSources)
        {
            try
            {
                var items = await FetchItemsFromFeedAsync(sourceName, feedUrl, tag, stoppingToken);
                rawCandidates.AddRange(items);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch RSS feed from {SourceName} ({FeedUrl})", sourceName, feedUrl);
            }
        }

        // Sort candidates by PublishDate descending
        var sortedCandidates = rawCandidates
            .OrderByDescending(c => c.PublishDate)
            .ToList();

        int insertedCount = 0;

        foreach (var candidate in sortedCandidates)
        {
            if (insertedCount >= maxItemsToInsert)
            {
                break;
            }

            // Check if link already exists in DB to prevent duplicates
            var alreadyExists = await db.Posts.AnyAsync(
                p => (p.Link != null && candidate.Link != null && p.Link == candidate.Link) ||
                     p.SinhalaTitle == candidate.Title,
                stoppingToken);

            if (alreadyExists)
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
                    CreatedAt = candidate.PublishDate > DateTime.MinValue ? candidate.PublishDate.UtcDateTime : DateTime.UtcNow
                };

                db.Posts.Add(newPost);
                await db.SaveChangesAsync(stoppingToken);
                insertedCount++;
                _logger.LogInformation("Inserted translated auto-news post: {Id} - {Title}", newPost.Id, newPost.SinhalaTitle);
            }
        }

        return insertedCount;
    }

    private async Task<List<RawFeedCandidate>> FetchItemsFromFeedAsync(
        string sourceName,
        string feedUrl,
        string defaultTag,
        CancellationToken stoppingToken)
    {
        var list = new List<RawFeedCandidate>();
        var httpClient = _httpClientFactory.CreateClient("RssClient");

        using var response = await httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
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

        foreach (var item in feed.Items.Take(6))
        {
            var title = item.Title?.Text ?? string.Empty;
            var rawSummary = item.Summary?.Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(rawSummary) && item.Content is TextSyndicationContent textContent)
            {
                rawSummary = textContent.Text;
            }

            var cleanSummary = StripHtml(rawSummary);
            var link = item.Links.FirstOrDefault()?.Uri?.ToString();
            var publishDate = item.PublishDate != DateTimeOffset.MinValue ? item.PublishDate : item.LastUpdatedTime;

            // Extract image URL from enclosure, media extensions, or raw HTML
            var imageUrl = ExtractImageUrl(item, rawSummary);

            if (!string.IsNullOrWhiteSpace(title))
            {
                list.Add(new RawFeedCandidate(sourceName, title.Trim(), cleanSummary, link, imageUrl, defaultTag, publishDate));
            }
        }

        return list;
    }

    private static string? ExtractImageUrl(SyndicationItem item, string rawContent)
    {
        // 1. Check enclosure links
        var enclosure = item.Links.FirstOrDefault(l =>
            l.RelationshipType?.Equals("enclosure", StringComparison.OrdinalIgnoreCase) == true &&
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
                ext.OuterName.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = ext.GetReader();
                    var url = reader.GetAttribute("url");
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
            var match = Regex.Match(rawContent, @"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                return match.Groups[1].Value;
            }
        }

        return null;
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
