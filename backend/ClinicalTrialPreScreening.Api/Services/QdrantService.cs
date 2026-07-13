using System.Net.Http.Json;

namespace ClinicalTrialPreScreening.Api.Services;

public record ProtocolChunkVectorPayload(Guid ProtocolId, string? SectionTitle, string ChunkText, int? PageNumber, string? CriterionId);

public record ChunkVectorItem(Guid ChunkId, IReadOnlyList<float> Vector, ProtocolChunkVectorPayload Payload);

// Isolates all Qdrant calls. Never throws: failures are logged and reported
// back as a bool so protocol upload can continue without vectors rather than
// fail the whole request.
public class QdrantService
{
    private const string CollectionName = "protocol_chunks";
    private const int VectorSize = 384; // sentence-transformers/all-MiniLM-L6-v2 output dimensionality

    private readonly HttpClient _httpClient;
    private readonly ILogger<QdrantService> _logger;

    public QdrantService(HttpClient httpClient, ILogger<QdrantService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var baseUrl = Environment.GetEnvironmentVariable("QDRANT_URL") ?? "http://qdrant:6333";
        _httpClient.BaseAddress = new Uri(baseUrl);
    }

    public async Task<bool> UpsertChunkVectorsAsync(IReadOnlyList<ChunkVectorItem> items)
    {
        if (items.Count == 0)
        {
            return true;
        }

        try
        {
            if (!await EnsureCollectionAsync())
            {
                return false;
            }

            var points = items.Select(item => new
            {
                id = item.ChunkId,
                vector = item.Vector,
                payload = new
                {
                    protocolId = item.Payload.ProtocolId,
                    sectionTitle = item.Payload.SectionTitle,
                    chunkText = item.Payload.ChunkText,
                    pageNumber = item.Payload.PageNumber,
                    criterionId = item.Payload.CriterionId
                }
            });

            var response = await _httpClient.PutAsJsonAsync($"/collections/{CollectionName}/points", new { points });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Qdrant upsert failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qdrant upsert call failed.");
            return false;
        }
    }

    private async Task<bool> EnsureCollectionAsync()
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"/collections/{CollectionName}", new
            {
                vectors = new { size = VectorSize, distance = "Cosine" }
            });

            // Qdrant's PUT-based collection creation is idempotent: it returns a
            // success status whether the collection was just created or already existed.
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogError("Failed to ensure Qdrant collection '{Collection}': HTTP {StatusCode}.", CollectionName, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qdrant collection setup call failed.");
            return false;
        }
    }
}
