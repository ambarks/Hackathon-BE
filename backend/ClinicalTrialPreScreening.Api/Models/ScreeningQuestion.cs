namespace ClinicalTrialPreScreening.Api.Models;

public class ScreeningQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProtocolId { get; set; }
    public string QuestionId { get; set; } = string.Empty; // e.g. "DEM-001" | "CRT-001"
    public string Section { get; set; } = string.Empty; // Demographics | Criteria
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerType { get; set; } = "yes_no"; // yes_no | single_choice | free_text | date | number
    public string? OptionsJson { get; set; }
    public string Priority { get; set; } = "Medium"; // High | Medium | Low
    public int DisplayOrder { get; set; }
    public string? LinkedCriteriaJson { get; set; } // JSON array of criterionId strings
    public string? CoveredCriteriaJson { get; set; } // JSON array of criterionId strings
    public string? SourceCriteriaText { get; set; }
    public string? WhyAsked { get; set; }
    public bool IsDemographicQuestion { get; set; }
    public bool IsDuplicateSuppressed { get; set; }
    public bool CanTriggerEarlyStop { get; set; }
    public string? EarlyStopReason { get; set; }

    // Denormalized from the linked criterion's AppliesToSex (only set when the
    // question has exactly one linked criterion carrying a Male/Female tag) so
    // the UI can show a badge without cross-referencing the criteria list.
    public string? AppliesToSex { get; set; }
    public string? PromptVersion { get; set; } // e.g. "question-bank-v1"
    public string? ModelName { get; set; } // actual Claude model, or a fallback-source label
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
