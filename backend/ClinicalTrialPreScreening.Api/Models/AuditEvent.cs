namespace ClinicalTrialPreScreening.Api.Models;

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty; // e.g. ProtocolUpload, CriteriaExtraction, CriteriaApproval, QuestionBankGeneration, DuplicateSuppression, ScreeningAnswer, SummaryGenerated
    public Guid? ProtocolId { get; set; }
    public Guid? SessionId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
