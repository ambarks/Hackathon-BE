using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Utils;

namespace ClinicalTrialPreScreening.Api.Services;

public record CriterionState(
    string CriterionId,
    string Type,
    string EligibilityImpact,
    string Status, // unanswered | satisfied | failed | needs_review
    bool CoveredByDemographics,
    string? ResolvedByQuestionId);

public record SessionStateSnapshot(
    IReadOnlyList<CriterionState> CriterionStates,
    IReadOnlyList<ScreeningQuestion> RemainingQuestions,
    ScreeningQuestion? NextQuestion,
    string CurrentSection,
    bool EarlyStopTriggered,
    string? EarlyStopReason,
    string? DisqualifyingCriterionId,
    string OverallLikelyStatus); // Pending | Likely Eligible | Likely Ineligible | Needs Clinical Review

// Deterministic, backend-controlled adaptive logic (prompt.txt section F).
// Claude never controls session flow directly: demographics are always asked
// first, then criteria questions in the question bank's generated display
// order. A question drops out of the remaining queue the instant any answer
// targeting it exists — duplicates are already excluded at question-bank
// generation time (Phase 5), so no separate "skip" pass is needed here.
public class AdaptiveQuestionService
{
    public SessionStateSnapshot ComputeState(
        IReadOnlyList<ScreeningQuestion> allQuestions,
        IReadOnlyList<EligibilityCriterion> allCriteria,
        IReadOnlyList<ScreeningAnswer> answers)
    {
        var criteriaById = allCriteria.ToDictionary(c => c.CriterionId);
        var stateByCriterion = allCriteria.ToDictionary(c => c.CriterionId, c => new MutableCriterionState(c));

        var latestAnswerByQuestionId = answers
            .GroupBy(a => a.QuestionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AnsweredAt).First());

        foreach (var question in allQuestions)
        {
            if (!latestAnswerByQuestionId.TryGetValue(question.QuestionId, out var answer))
            {
                continue;
            }

            foreach (var criterionId in JsonHelpers.DeserializeStringArray(question.LinkedCriteriaJson))
            {
                if (!criteriaById.TryGetValue(criterionId, out var criterion) ||
                    !stateByCriterion.TryGetValue(criterionId, out var mutableState))
                {
                    continue;
                }

                mutableState.Status = DetermineMappedStatus(criterion, answer.ResponseText);
                mutableState.ResolvedByQuestionId = question.QuestionId;
                mutableState.CoveredByDemographics = question.IsDemographicQuestion;
            }
        }

        var states = stateByCriterion.Values.Select(s => s.Build()).ToList();

        // The backend — never Claude — decides whether screening should stop:
        // a failed criterion whose EligibilityImpact is Exclusionary or Required
        // means the result is already determined regardless of any remaining
        // answers (e.g. age 16 vs a required 18-75 range). This is a fixed rule
        // over the (possibly AI-assisted, see AnswerEvaluationService) status
        // already computed above, not a Claude decision itself.
        var disqualifyingState = states.FirstOrDefault(s =>
            s.Status == "failed" &&
            (s.EligibilityImpact.Equals("Exclusionary", StringComparison.OrdinalIgnoreCase) ||
             s.EligibilityImpact.Equals("Required", StringComparison.OrdinalIgnoreCase)));

        var earlyStopTriggered = disqualifyingState is not null;
        string? earlyStopReason = null;
        string? disqualifyingCriterionId = disqualifyingState?.CriterionId;

        if (disqualifyingState is not null)
        {
            var resolvingAnswer = disqualifyingState.ResolvedByQuestionId is not null &&
                latestAnswerByQuestionId.TryGetValue(disqualifyingState.ResolvedByQuestionId, out var answerForCriterion)
                ? answerForCriterion
                : null;

            if (!string.IsNullOrWhiteSpace(resolvingAnswer?.AiReasoning))
            {
                earlyStopReason = resolvingAnswer!.AiReasoning;
            }
            else
            {
                var criterionText = criteriaById.TryGetValue(disqualifyingState.CriterionId, out var criterion)
                    ? criterion.SimpleMeaning ?? criterion.OriginalText
                    : disqualifyingState.CriterionId;
                earlyStopReason = $"The recorded answer does not meet criterion {disqualifyingState.CriterionId}: {criterionText}";
            }
        }

        var remaining = allQuestions
            .Where(q => !latestAnswerByQuestionId.ContainsKey(q.QuestionId))
            .OrderBy(q => q.IsDemographicQuestion ? 0 : 1)
            .ThenBy(q => q.DisplayOrder)
            .ToList();

        var nextQuestion = remaining.FirstOrDefault();
        var currentSection = nextQuestion?.Section ?? "Completed";
        var overallStatus = DetermineOverallStatus(states, allAnswered: remaining.Count == 0);

        return new SessionStateSnapshot(
            states, remaining, nextQuestion, currentSection, earlyStopTriggered, earlyStopReason, disqualifyingCriterionId, overallStatus);
    }

    // Heuristic: the first word of a free-text response is checked for yes/no;
    // polarity then depends on whether the linked criterion is an inclusion
    // (yes => satisfied) or exclusion (yes => failed) requirement. Anything else
    // (numbers, dates, "not sure", free text) is conservatively routed to
    // needs_review rather than guessed at — consistent with the project's
    // "recruiter/clinical reviewer makes the final call" guardrail.
    public static string DetermineMappedStatus(EligibilityCriterion criterion, string responseText)
    {
        var firstWord = responseText
            .Trim()
            .Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.ToLowerInvariant();

        var isExclusion = criterion.Type.Equals("Exclusion", StringComparison.OrdinalIgnoreCase);

        string status;
        if (firstWord == "yes")
        {
            status = isExclusion ? "failed" : "satisfied";
        }
        else if (firstWord == "no")
        {
            status = isExclusion ? "satisfied" : "failed";
        }
        else
        {
            status = "needs_review";
        }

        // Criteria based on values a patient may not know precisely (e.g. lab
        // results) still get routed to clinical review even on an apparent pass.
        if (criterion.RequiresClinicalReview && status == "satisfied")
        {
            status = "needs_review";
        }

        return status;
    }

    public static string SummarizeStatus(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0) return "unanswered";
        if (list.Contains("failed")) return "failed";
        if (list.Contains("needs_review")) return "needs_review";
        return list.All(s => s == "satisfied") ? "satisfied" : "needs_review";
    }

    private static string DetermineOverallStatus(IReadOnlyList<CriterionState> states, bool allAnswered)
    {
        if (states.Any(s => s.Status == "failed"))
        {
            return "Likely Ineligible";
        }

        if (states.Any(s => s.Status == "needs_review"))
        {
            return "Needs Clinical Review";
        }

        return allAnswered ? "Likely Eligible" : "Pending";
    }

    private class MutableCriterionState
    {
        private readonly EligibilityCriterion _criterion;

        public MutableCriterionState(EligibilityCriterion criterion)
        {
            _criterion = criterion;
        }

        public string Status { get; set; } = "unanswered";
        public bool CoveredByDemographics { get; set; }
        public string? ResolvedByQuestionId { get; set; }

        public CriterionState Build() => new(
            _criterion.CriterionId,
            _criterion.Type,
            _criterion.EligibilityImpact,
            Status,
            CoveredByDemographics,
            ResolvedByQuestionId);
    }
}
