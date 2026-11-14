using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace cd_newsfeed.Services;

/// <summary>Creates local editorial illustrations for stories without a usable publisher image.</summary>
public sealed class GeminiImageGenerator
{
    private static int _preferredApiKeyIndex;
    private static readonly string[] Models = ["gemini-3.1-flash-image", "gemini-2.5-flash-image"];
    private readonly HttpClient _httpClient;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<GeminiImageGenerator> _logger;

    public GeminiImageGenerator(
        HttpClient httpClient,
        IWebHostEnvironment environment,
        ILogger<GeminiImageGenerator> logger)
    {
        _httpClient = httpClient;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string?> GenerateAndSaveAsync(
        string title,
        string summary,
        IReadOnlyList<string> apiKeys,
        CancellationToken cancellationToken = default)
    {
        if (apiKeys.Count == 0 || string.IsNullOrWhiteSpace(_environment.WebRootPath))
            return null;

        var prompt = $"""
            Create a polished editorial cover illustration for a technology and AI news article.
            Use a cinematic, professional magazine style, a clear central visual metaphor,
            rich but restrained colors, landscape 16:9 composition, no words, no letters,
            no logos, and no watermarks. Depict the topic accurately without presenting
            the illustration as a real photograph.
            Headline: {Limit(title, 300)}
            Context: {Limit(summary, 700)}
            """;

        var startIndex = Math.Abs(Volatile.Read(ref _preferredApiKeyIndex)) % apiKeys.Count;
        foreach (var model in Models)
        {
            for (var offset = 0; offset < apiKeys.Count; offset++)
            {
                var keyIndex = (startIndex + offset) % apiKeys.Count;
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
                request.Headers.Add("x-goog-api-key", apiKeys[keyIndex]);
                request.Content = JsonContent.Create(new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new
                    {
                        responseModalities = new[] { "IMAGE" },
                        imageConfig = new { aspectRatio = "16:9", imageSize = "1K" }
                    }
                });

                try
                {
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning(
                            "Gemini image generation model {Model} returned HTTP {StatusCode} for key slot {KeySlot}.",
                            model, (int)response.StatusCode, keyIndex + 1);
                        if (IsQuotaExceeded(response.StatusCode, error))
                            Interlocked.Exchange(ref _preferredApiKeyIndex, (keyIndex + 1) % apiKeys.Count);
                        continue;
                    }

                    using var document = await JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(cancellationToken),
                        cancellationToken: cancellationToken);
                    if (!TryGetImage(document.RootElement, out var bytes, out var extension))
                    {
                        _logger.LogWarning("Gemini image model {Model} returned no image data.", model);
                        continue;
                    }

                    var directory = Path.Combine(_environment.WebRootPath, "uploads", "articles", "generated");
                    Directory.CreateDirectory(directory);
                    var fileName = $"{Guid.NewGuid():N}.{extension}";
                    await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes, cancellationToken);
                    Interlocked.Exchange(ref _preferredApiKeyIndex, keyIndex);
                    return $"/uploads/articles/generated/{fileName}";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gemini image generation failed for model {Model}, key slot {KeySlot}.", model, keyIndex + 1);
                }
            }
        }

        return null;
    }

    private static bool TryGetImage(JsonElement root, out byte[] bytes, out string extension)
    {
        bytes = [];
        extension = "png";
        if (!root.TryGetProperty("candidates", out var candidates)) return false;
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts)) continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out var inlineData) &&
                    !part.TryGetProperty("inline_data", out inlineData)) continue;
                if (!inlineData.TryGetProperty("data", out var data)) continue;
                var encoded = data.GetString();
                if (string.IsNullOrWhiteSpace(encoded)) continue;
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length == 0 || bytes.Length > 12 * 1024 * 1024) return false;
                    var mimeType = inlineData.TryGetProperty("mimeType", out var mime) ||
                                   inlineData.TryGetProperty("mime_type", out mime)
                        ? mime.GetString()
                        : null;
                    extension = mimeType?.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) == true ? "jpg" : "png";
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }
        return false;
    }

    private static bool IsQuotaExceeded(HttpStatusCode status, string body) =>
        status == HttpStatusCode.TooManyRequests ||
        (status == HttpStatusCode.Forbidden &&
         (body.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
          body.Contains("QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase) ||
          body.Contains("quota", StringComparison.OrdinalIgnoreCase)));

    private static string Limit(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
