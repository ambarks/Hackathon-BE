namespace ClinicalTrialPreScreening.Api.Models;

public class ScreeningSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProtocolId { get; set; }
    public string PatientAlias { get; set; } = string.Empty;
    public string CurrentSection { get; set; } = "Demographics"; // Demographics | Criteria
    public string Status { get; set; } = "InProgress"; // InProgress | Completed
    public string? OverallLikelyStatus { get; set; } // Likely Eligible | Likely Ineligible | Needs Clinical Review
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
