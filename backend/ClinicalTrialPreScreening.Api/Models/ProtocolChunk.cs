namespace ClinicalTrialPreScreening.Api.Models;

public class ProtocolChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProtocolId { get; set; }
    public int ChunkIndex { get; set; }
    public string? SectionTitle { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public string? CriterionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
