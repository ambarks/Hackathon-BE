using System.Net.Http.Json;

namespace ClinicalTrialPreScreening.Api.Services;

public record EmbeddingResult(IReadOnlyList<IReadOnlyList<float>> Embeddings, string Model, int Dimensions);

// Isolates all calls to the Python embedding microservice. Never throws: a
// failure is logged and returns null so callers can decide how to proceed.
public class EmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbeddingClient> _logger;

    public EmbeddingClient(HttpClient httpClient, ILogger<EmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var baseUrl = Environment.GetEnvironmentVariable("EMBEDDING_SERVICE_URL") ?? "http://embedding-service:8001";
        _httpClient.BaseAddress = new Uri(baseUrl);
    }

    public async Task<EmbeddingResult?> EmbedAsync(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
        {
            return new EmbeddingResult(Array.Empty<IReadOnlyList<float>>(), "n/a", 0);
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/embed", new { texts });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Embedding service returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<EmbedResponsePayload>();
            if (payload?.Embeddings is null)
            {
                _logger.LogError("Embedding service returned an unexpected response shape.");
                return null;
            }

            return new EmbeddingResult(
                payload.Embeddings.Select(e => (IReadOnlyList<float>)e).ToList(),
                payload.Model ?? "unknown",
                payload.Dimensions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding service call failed.");
            return null;
        }
    }

    private class EmbedResponsePayload
    {
        public List<List<float>>? Embeddings { get; set; }
        public string? Model { get; set; }
        public int Dimensions { get; set; }
    }
}
