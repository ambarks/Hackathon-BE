using ClinicalTrialPreScreening.Api.Data;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalTrialPreScreening.Api.Controllers;

[ApiController]
[Route("api/protocols")]
public class ProtocolsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ProtocolTextExtractionService _extractionService;
    private readonly ProtocolChunkingService _chunkingService;
    private readonly EmbeddingClient _embeddingClient;
    private readonly QdrantService _qdrantService;
    private readonly AuditService _auditService;
    private readonly ILogger<ProtocolsController> _logger;

    public ProtocolsController(
        AppDbContext db,
        ProtocolTextExtractionService extractionService,
        ProtocolChunkingService chunkingService,
        EmbeddingClient embeddingClient,
        QdrantService qdrantService,
        AuditService auditService,
        ILogger<ProtocolsController> logger)
    {
        _db = db;
        _extractionService = extractionService;
        _chunkingService = chunkingService;
        _embeddingClient = embeddingClient;
        _qdrantService = qdrantService;
        _auditService = auditService;
        _logger = logger;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "A PDF file is required." });
        }

        await using var stream = file.OpenReadStream();
        var extraction = await _extractionService.ExtractFromPdfAsync(stream, file.FileName);

        var protocol = await PersistProtocolAsync(file.FileName, "upload", extraction.Text);

        await _auditService.LogEventAsync(
            "ProtocolUpload",
            protocolId: protocol.Id,
            details: $"source=upload; fileName={file.FileName}; usedFallback={extraction.UsedFallback}");

        return Ok(ToProtocolResponse(protocol, extraction.UsedFallback, extraction.FallbackReason));
    }

    [HttpPost("use-sample")]
    public async Task<IActionResult> UseSample()
    {
        var text = await _extractionService.GetSampleProtocolTextAsync();
        var protocol = await PersistProtocolAsync("sample-protocol.txt", "sample", text);

        await _auditService.LogEventAsync("ProtocolUpload", protocolId: protocol.Id, details: "source=sample");

        return Ok(ToProtocolResponse(protocol, usedFallback: false, fallbackReason: null));
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var protocols = await _db.Protocols
            .OrderByDescending(p => p.UploadedAt)
            .Select(p => new ProtocolListItemResponse(p.Id, p.FileName, p.Source, p.Status, p.UploadedAt))
            .ToListAsync();

        return Ok(protocols);
    }

    [HttpGet("{protocolId:guid}")]
    public async Task<IActionResult> GetById(Guid protocolId)
    {
        var protocol = await _db.Protocols.FindAsync(protocolId);
        if (protocol is null)
        {
            return NotFound(new { message = $"Protocol {protocolId} was not found." });
        }

        var chunks = await _db.ProtocolChunks
            .Where(c => c.ProtocolId == protocolId)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new ProtocolChunkResponse(c.Id, c.ChunkIndex, c.SectionTitle, c.ChunkText, c.PageNumber, c.CriterionId))
            .ToListAsync();

        return Ok(ToProtocolResponse(protocol, usedFallback: false, fallbackReason: null) with { Chunks = chunks });
    }

    private async Task<Protocol> PersistProtocolAsync(string? fileName, string source, string text)
    {
        var protocol = new Protocol
        {
            FileName = fileName,
            Source = source,
            RawText = text,
            Status = "Uploaded"
        };

        _db.Protocols.Add(protocol);

        var chunkData = _chunkingService.ChunkText(text);
        var chunkEntities = new List<ProtocolChunk>();
        foreach (var chunk in chunkData)
        {
            var entity = new ProtocolChunk
            {
                ProtocolId = protocol.Id,
                ChunkIndex = chunk.ChunkIndex,
                SectionTitle = chunk.SectionTitle,
                ChunkText = chunk.ChunkText,
                PageNumber = chunk.PageNumber
            };
            chunkEntities.Add(entity);
            _db.ProtocolChunks.Add(entity);
        }

        await _db.SaveChangesAsync();

        await EmbedAndStoreVectorsAsync(protocol.Id, chunkEntities);

        return protocol;
    }

    // Best-effort: embeds chunk text via EmbeddingClient and stores vectors in
    // Qdrant via QdrantService. Never throws — if either service is
    // unavailable, the protocol upload has already succeeded and this simply
    // logs a warning and skips vector storage for this protocol.
    private async Task EmbedAndStoreVectorsAsync(Guid protocolId, IReadOnlyList<ProtocolChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        var embeddingResult = await _embeddingClient.EmbedAsync(chunks.Select(c => c.ChunkText).ToList());
        if (embeddingResult is null || embeddingResult.Embeddings.Count != chunks.Count)
        {
            _logger.LogWarning(
                "Embedding unavailable or incomplete for protocol {ProtocolId}; skipping vector storage for this upload.",
                protocolId);
            return;
        }

        var items = chunks
            .Zip(embeddingResult.Embeddings, (chunk, vector) => new ChunkVectorItem(
                chunk.Id,
                vector,
                new ProtocolChunkVectorPayload(protocolId, chunk.SectionTitle, chunk.ChunkText, chunk.PageNumber, chunk.CriterionId)))
            .ToList();

        var success = await _qdrantService.UpsertChunkVectorsAsync(items);
        if (!success)
        {
            _logger.LogWarning("Failed to store vectors in Qdrant for protocol {ProtocolId}.", protocolId);
        }
    }

    private static ProtocolResponse ToProtocolResponse(Protocol protocol, bool usedFallback, string? fallbackReason) =>
        new(protocol.Id, protocol.FileName, protocol.Source, protocol.Status, protocol.UploadedAt, usedFallback, fallbackReason, Chunks: null);
}

public record ProtocolResponse(
    Guid ProtocolId,
    string? FileName,
    string Source,
    string Status,
    DateTime UploadedAt,
    bool UsedFallback,
    string? FallbackReason,
    IReadOnlyList<ProtocolChunkResponse>? Chunks);

public record ProtocolChunkResponse(Guid Id, int ChunkIndex, string? SectionTitle, string ChunkText, int? PageNumber, string? CriterionId);

public record ProtocolListItemResponse(Guid ProtocolId, string? FileName, string Source, string Status, DateTime UploadedAt);
