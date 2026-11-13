using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace cd_newsfeed.Services;

public record TranslationResult(string SinhalaTitle, string SinhalaSummary, string? SinhalaContent);

public class GeminiTranslatorService
{
    private static int _preferredApiKeyIndex;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GeminiTranslatorService> _logger;

    public GeminiTranslatorService(HttpClient httpClient, ILogger<GeminiTranslatorService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TranslationResult?> TranslateNewsAsync(string title, string summary, IReadOnlyList<string> apiKeys, CancellationToken cancellationToken = default)
    {
        if (apiKeys.Count == 0)
        {
            return null;
        }

        try
        {
            const string endpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent";

            var promptText = $@"You are an expert bilingual tech journalist and translator fluent in English and Sinhala.
Translate and summarize the following English technology/AI news into natural, modern, high-quality Sinhala:
Original Title: {title}
Original Content/Summary: {summary}

Respond ONLY with a valid JSON object matching this structure:
{{
  ""sinhalaTitle"": ""... (concise, compelling Sinhala headline)"",
  ""sinhalaSummary"": ""... (2-3 clear, informative sentences in natural Sinhala explaining the key points)"",
  ""sinhalaContent"": ""... (detailed, comprehensive 2-4 paragraph full article in natural Sinhala explaining background, technical details, features, and future impact so readers can read the complete article within the app)""
}}";

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = promptText }
                        }
                    }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json"
                }
            };

            var startIndex = Math.Abs(Volatile.Read(ref _preferredApiKeyIndex)) % apiKeys.Count;
            for (var offset = 0; offset < apiKeys.Count; offset++)
            {
                var keyIndex = (startIndex + offset) % apiKeys.Count;
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("x-goog-api-key", apiKeys[keyIndex]);
                request.Content = JsonContent.Create(requestBody);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (IsQuotaExceeded(response.StatusCode, errorBody))
                    {
                        var nextIndex = (keyIndex + 1) % apiKeys.Count;
                        Interlocked.Exchange(ref _preferredApiKeyIndex, nextIndex);
                        _logger.LogWarning("Gemini API key slot {KeySlot} is quota-limited; trying the next configured key.", keyIndex + 1);
                        continue;
                    }

                    _logger.LogWarning("Gemini API call failed with status {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                    return null;
                }

                Interlocked.Exchange(ref _preferredApiKeyIndex, keyIndex);
                using var responseDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = responseDoc.RootElement;

                if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Gemini returned no candidates in response.");
                    return null;
                }

                var firstCandidate = candidates[0];
                if (!firstCandidate.TryGetProperty("content", out var content) ||
                    !content.TryGetProperty("parts", out var parts) ||
                    parts.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Gemini response missing content parts.");
                    return null;
                }

                var jsonText = parts[0].GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    return null;
                }

                // Clean markdown code blocks if returned
                jsonText = jsonText.Trim();
                if (jsonText.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                {
                    jsonText = jsonText.Substring(7);
                }
                if (jsonText.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    jsonText = jsonText.Substring(3);
                }
                if (jsonText.EndsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    jsonText = jsonText.Substring(0, jsonText.Length - 3);
                }
                jsonText = jsonText.Trim();

                using var parsedResult = JsonDocument.Parse(jsonText);
                var resultRoot = parsedResult.RootElement;

                string sinhalaTitle = string.Empty;
                string sinhalaSummary = string.Empty;
                string? sinhalaContent = null;

                if (resultRoot.TryGetProperty("sinhalaTitle", out var titleProp))
                {
                    sinhalaTitle = titleProp.GetString() ?? string.Empty;
                }
                if (resultRoot.TryGetProperty("sinhalaSummary", out var summaryProp))
                {
                    sinhalaSummary = summaryProp.GetString() ?? string.Empty;
                }
                if (resultRoot.TryGetProperty("sinhalaContent", out var contentProp))
                {
                    sinhalaContent = contentProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(sinhalaTitle) && !string.IsNullOrWhiteSpace(sinhalaSummary))
                {
                    return new TranslationResult(sinhalaTitle.Trim(), sinhalaSummary.Trim(), sinhalaContent?.Trim());
                }

                _logger.LogWarning("Gemini result was missing expected fields: {JsonText}", jsonText);
                return null;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while translating news item via Gemini API: {Title}", title);
            return null;
        }
    }

    private static bool IsQuotaExceeded(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return statusCode == HttpStatusCode.Forbidden &&
            (responseBody.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
    }
}
