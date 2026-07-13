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

    // How MappedEligibilityStatus was determined for this answer: "deterministic"
    // (the existing yes/no heuristic, for questions that cannot trigger early
    // stop), "ai" (Claude-assisted interpretation, for CanTriggerEarlyStop
    // questions), or "fallback-rule" (Claude was unavailable/unparseable and the
    // deterministic heuristic was used as a fallback for a CanTriggerEarlyStop
    // question). Claude never decides eligibility itself; it only assists in
    // classifying the answer text against the criterion (see plan2.md).
    public string EvaluationSource { get; set; } = "deterministic";
    public string? AiReasoning { get; set; }
    public string? PromptVersion { get; set; } // e.g. "answer-evaluation-v1"
    public string? ModelName { get; set; } // actual Claude model, or a fallback-source label
}
