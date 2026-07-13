namespace ClinicalTrialPreScreening.Api.Models;

public class ScreeningAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public string? LinkedCriteriaJson { get; set; } // JSON array of criterionId strings
    public string Section { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public DateTime AnsweredAt { get; set; } = DateTime.UtcNow;
    public string? MappedEligibilityStatus { get; set; } // satisfied | failed | needs_review | unanswered
    public bool CoveredMultipleCriteria { get; set; }
    public bool SkippedDueToDemographics { get; set; }
}
