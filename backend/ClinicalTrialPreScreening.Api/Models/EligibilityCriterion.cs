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

    // Null/omitted means the criterion applies to every patient regardless of
    // sex (the common case). Only set to "Male"/"Female" for criteria that are
    // biologically irrelevant to the other sex (e.g. pregnancy, prostate) — not
    // for a protocol-level "this trial only enrolls X" restriction, which is
    // just a normal criterion resolved like any other. See plan3.md.
    public string? AppliesToSex { get; set; }

    public bool IsApproved { get; set; }
    public string? PromptVersion { get; set; } // e.g. "criteria-extraction-v1"
    public string? ModelName { get; set; } // actual Claude model, or a fallback-source label
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
