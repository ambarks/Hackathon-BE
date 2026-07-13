namespace ClinicalTrialPreScreening.Api.Models;

public class EligibilityCriterion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProtocolId { get; set; }
    public string CriterionId { get; set; } = string.Empty; // business code, e.g. "INC-001"
    public string Type { get; set; } = string.Empty; // Inclusion | Exclusion
    public string OriginalText { get; set; } = string.Empty;
    public string? SimpleMeaning { get; set; }
    public string? PatientQuestion { get; set; }
    public string AnswerType { get; set; } = "yes_no"; // yes_no | single_choice | free_text | date | number
    public string? OptionsJson { get; set; }
    public string EligibilityImpact { get; set; } = "Required"; // Required | Exclusionary | Needs Review
    public string Priority { get; set; } = "Medium"; // High | Medium | Low
    public string? SourceSection { get; set; }
    public bool RequiresClinicalReview { get; set; }
    public bool CanBeCoveredByDemographics { get; set; }
    public bool IsApproved { get; set; }
    public string? PromptVersion { get; set; } // e.g. "criteria-extraction-v1"
    public string? ModelName { get; set; } // actual Claude model, or a fallback-source label
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
