namespace ClinicalTrialPreScreening.Api.Models;

public class ScreeningSummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Recommendation { get; set; } = string.Empty; // Likely Eligible | Likely Ineligible | Needs Clinical Review
    public string? SummaryText { get; set; }
    public string? DemographicSummaryJson { get; set; }
    public string? CriteriaSummaryJson { get; set; }
    public string? CriteriaCoveredByDemographicsJson { get; set; }
    public string? SatisfiedCriteriaJson { get; set; }
    public string? FailedCriteriaJson { get; set; }
    public string? NeedsReviewCriteriaJson { get; set; }
    public string? SkippedCriteriaJson { get; set; }
    public string? MissingInformationJson { get; set; }
    public string? ReasoningJson { get; set; }
    public string? RecommendedNextAction { get; set; }
    public string Disclaimer { get; set; } =
        "This is AI-assisted pre-screening guidance. Final eligibility must be confirmed by qualified clinical staff.";
    public string? PromptVersion { get; set; }
    public string? ModelName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
