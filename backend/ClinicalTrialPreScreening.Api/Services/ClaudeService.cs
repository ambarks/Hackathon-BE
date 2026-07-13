using System.Text;
using System.Text.Json;

namespace ClinicalTrialPreScreening.Api.Services;

// Isolates all Claude API calls. Every prompt sent through SendAsync should
// already include the required guardrails (see CLAUDE.md / prompt.txt).
// SendAsync must never throw on a Claude-side failure: it logs and returns
// null so callers can fall back to sample data / rule-based logic.
public class ClaudeService
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaudeService> _logger;
    private readonly AiStatusService _aiStatusService;
    private readonly string? _apiKey;
    private readonly string _model;

    // The configured Claude model name, for tagging AI-generated outputs with provenance.
    public string Model => _model;

    public ClaudeService(HttpClient httpClient, ILogger<ClaudeService> logger, AiStatusService aiStatusService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _aiStatusService = aiStatusService;

        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-3-5-sonnet-latest";

        _aiStatusService.SetConfigured(IsApiKeyConfigured(_apiKey), _model);
    }

    public async Task<string?> SendAsync(string systemPrompt, string userPrompt, int maxTokens = 4096)
    {
        if (!IsApiKeyConfigured(_apiKey))
        {
            _aiStatusService.RecordFallback("Claude API key is not configured. Running in local fallback demo mode.");
            return null;
        }

        var requestBody = new
        {
            model = _model,
            max_tokens = maxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await _httpClient.SendAsync(request);
            responseBody = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude API request failed (network/transport error). Running in fallback mode.");
            _aiStatusService.RecordFallback("Claude API request failed. Running in fallback mode.");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Claude API returned a non-JSON response. Running in fallback mode.");
            _aiStatusService.RecordFallback("Claude API returned an unexpected response. Running in fallback mode.");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            var isErrorShaped = IsErrorShaped(root, out var errorMessage);
            if (!response.IsSuccessStatusCode || isErrorShaped)
            {
                if (IsInsufficientCreditsMessage(errorMessage))
                {
                    const string message = "Claude API configured but API call failed due to insufficient credits. Running in fallback mode.";
                    _logger.LogError(message);
                    _aiStatusService.RecordFallback(message);
                }
                else
                {
                    var message = $"Claude API call failed (HTTP {(int)response.StatusCode}): {errorMessage ?? "unknown error"}. Running in fallback mode.";
                    _logger.LogError(message);
                    _aiStatusService.RecordFallback(message);
                }

                return null;
            }

            if (!root.TryGetProperty("content", out var contentArray) ||
                contentArray.ValueKind != JsonValueKind.Array ||
                contentArray.GetArrayLength() == 0)
            {
                _logger.LogError("Claude API response did not contain a content array. Running in fallback mode.");
                _aiStatusService.RecordFallback("Claude API returned an unexpected response shape. Running in fallback mode.");
                return null;
            }

            var firstBlock = contentArray[0];
            if (!firstBlock.TryGetProperty("text", out var textElement))
            {
                _logger.LogError("Claude API response content block did not contain a text field. Running in fallback mode.");
                _aiStatusService.RecordFallback("Claude API returned an unexpected response shape. Running in fallback mode.");
                return null;
            }

            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogError("Claude API response text was empty. Running in fallback mode.");
                _aiStatusService.RecordFallback("Claude API returned an empty response. Running in fallback mode.");
                return null;
            }

            _aiStatusService.RecordSuccess();
            return text;
        }
    }

    private static bool IsApiKeyConfigured(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && !apiKey.StartsWith("replace_with", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorShaped(JsonElement root, out string? errorMessage)
    {
        errorMessage = null;

        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            typeElement.GetString() == "error" &&
            root.TryGetProperty("error", out var errorElement))
        {
            if (errorElement.TryGetProperty("message", out var messageElement))
            {
                errorMessage = messageElement.GetString();
            }

            return true;
        }

        return false;
    }

    private static bool IsInsufficientCreditsMessage(string? message) =>
        !string.IsNullOrEmpty(message) && message.Contains("credit", StringComparison.OrdinalIgnoreCase);
}
