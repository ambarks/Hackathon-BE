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

    // Deterministic, backend-computed recommendation (never decided by Claude
    // directly) that the screening result is already final based on answers so
    // far — e.g. a failed Exclusionary or Required criterion. This never blocks
    // or auto-completes the session; it only surfaces a recommendation the
    // recruiter can act on via POST /{sessionId}/end, or dismiss and continue.
    public bool EarlyStopRecommended { get; set; }
    public string? EarlyStopReason { get; set; }
    public string? DisqualifyingCriterionId { get; set; }
    public DateTime? EarlyStopDetectedAt { get; set; }
}
