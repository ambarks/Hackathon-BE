using System.Text.Json;
using ClinicalTrialPreScreening.Api.Data;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace ClinicalTrialPreScreening.Api.Services;

public record DemographicAnswerSummary(string QuestionId, string QuestionText, string ResponseText, IReadOnlyList<string> CoveredCriteria);

public record CriterionSummary(string CriterionId, string Type, string Status, string? ResolvedByQuestionId);

public record SummaryResult(
    string Recommendation,
    string SummaryText,
    IReadOnlyList<DemographicAnswerSummary> DemographicSummary,
    IReadOnlyList<CriterionSummary> CriteriaSummary,
    IReadOnlyList<string> CriteriaCoveredByDemographics,
    IReadOnlyList<string> SatisfiedCriteria,
    IReadOnlyList<string> FailedCriteria,
    IReadOnlyList<string> NeedsReviewCriteria,
    IReadOnlyList<string> SkippedCriteria,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<string> Reasoning,
    string RecommendedNextAction,
    string Disclaimer,
    bool UsedFallback,
    string? FallbackReason);

// Owns screening-session lifecycle and deterministic state (prompt.txt section
// F): creating sessions, recomputing criterion/section state via
// AdaptiveQuestionService, persisting answers, and generating the final
// recommendation summary (Claude-assisted narrative with a rule-based fallback).
public class ScreeningSessionService
{
    private const string SummaryPromptVersion = "summary-v1";
    private const string FallbackModelLabel = "fallback-rule-based-summary";
    private const string SummaryDisclaimer =
        "This is AI-assisted pre-screening guidance. Final eligibility must be confirmed by qualified clinical staff.";

    private readonly AppDbContext _db;
    private readonly AdaptiveQuestionService _adaptiveQuestionService;
    private readonly ClaudeService _claudeService;
    private readonly ILogger<ScreeningSessionService> _logger;

    public ScreeningSessionService(
        AppDbContext db,
        AdaptiveQuestionService adaptiveQuestionService,
        ClaudeService claudeService,
        ILogger<ScreeningSessionService> logger)
    {
        _db = db;
        _adaptiveQuestionService = adaptiveQuestionService;
        _claudeService = claudeService;
        _logger = logger;
    }

    public async Task<ScreeningSession> CreateSessionAsync(Guid protocolId, string patientAlias)
    {
        var session = new ScreeningSession
        {
            ProtocolId = protocolId,
            PatientAlias = patientAlias,
            CurrentSection = "Demographics",
            Status = "InProgress"
        };

        _db.ScreeningSessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    public async Task<SessionStateSnapshot> GetStateAsync(Guid sessionId)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId)
            ?? throw new KeyNotFoundException($"Screening session {sessionId} was not found.");

        var questions = await LoadQuestionsAsync(session.ProtocolId);
        var criteria = await LoadCriteriaAsync(session.ProtocolId);
        var answers = await LoadAnswersAsync(sessionId);

        return _adaptiveQuestionService.ComputeState(questions, criteria, answers);
    }

    public async Task<ScreeningAnswer> RecordAnswerAsync(Guid sessionId, string questionId, string responseText)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId)
            ?? throw new KeyNotFoundException($"Screening session {sessionId} was not found.");

        var question = await _db.ScreeningQuestions
            .FirstOrDefaultAsync(q => q.ProtocolId == session.ProtocolId && q.QuestionId == questionId)
            ?? throw new KeyNotFoundException($"Question {questionId} was not found for this session's protocol.");

        var linkedCriteriaIds = JsonHelpers.DeserializeStringArray(question.LinkedCriteriaJson);
        var linkedCriteria = await _db.EligibilityCriteria
            .Where(c => c.ProtocolId == session.ProtocolId && linkedCriteriaIds.Contains(c.CriterionId))
            .ToListAsync();

        var perCriterionStatuses = linkedCriteria
            .Select(c => AdaptiveQuestionService.DetermineMappedStatus(c, responseText))
            .ToList();

        var answer = new ScreeningAnswer
        {
            SessionId = sessionId,
            QuestionId = questionId,
            LinkedCriteriaJson = question.LinkedCriteriaJson,
            Section = question.Section,
            QuestionText = question.QuestionText,
            ResponseText = responseText,
            MappedEligibilityStatus = AdaptiveQuestionService.SummarizeStatus(perCriterionStatuses),
            CoveredMultipleCriteria = linkedCriteriaIds.Count > 1,
            SkippedDueToDemographics = false
        };

        _db.ScreeningAnswers.Add(answer);
        await _db.SaveChangesAsync();

        // Recompute and persist the session's live section/status after this answer.
        var state = await GetStateAsync(sessionId);
        session.CurrentSection = state.CurrentSection;
        session.OverallLikelyStatus = state.OverallLikelyStatus;

        if (state.NextQuestion is null)
        {
            session.Status = "Completed";
            session.CompletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        return answer;
    }

    // Generating a summary also finalizes the session: if the recruiter ends
    // early (some questions still unanswered), those criteria are reported as
    // skipped/missing rather than blocking the recommendation (prompt.txt
    // section F: "allow recruiter to continue or end session").
    public async Task<SummaryResult> GenerateSummaryAsync(Guid sessionId)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId)
            ?? throw new KeyNotFoundException($"Screening session {sessionId} was not found.");

        var questions = await LoadQuestionsAsync(session.ProtocolId);
        var criteria = await LoadCriteriaAsync(session.ProtocolId);
        var answers = await LoadAnswersAsync(sessionId);
        var criteriaById = criteria.ToDictionary(c => c.CriterionId);

        var state = _adaptiveQuestionService.ComputeState(questions, criteria, answers);

        if (session.Status != "Completed")
        {
            session.Status = "Completed";
            session.CompletedAt = DateTime.UtcNow;
        }

        session.CurrentSection = state.CurrentSection;

        // A summary always needs one of the three final categories, never the
        // live in-progress "Pending" marker used by next-question/section-progress.
        var recommendation = state.OverallLikelyStatus == "Pending" ? "Needs Clinical Review" : state.OverallLikelyStatus;
        session.OverallLikelyStatus = recommendation;

        var answersByQuestionId = answers.ToDictionary(a => a.QuestionId);

        var demographicSummary = questions
            .Where(q => q.IsDemographicQuestion && answersByQuestionId.ContainsKey(q.QuestionId))
            .Select(q => new DemographicAnswerSummary(
                q.QuestionId,
                q.QuestionText,
                answersByQuestionId[q.QuestionId].ResponseText,
                JsonHelpers.DeserializeStringArray(q.CoveredCriteriaJson)))
            .ToList();

        var criteriaSummary = state.CriterionStates
            .Select(s => new CriterionSummary(s.CriterionId, s.Type, s.Status, s.ResolvedByQuestionId))
            .ToList();

        var criteriaCoveredByDemographics = state.CriterionStates
            .Where(s => s.CoveredByDemographics)
            .Select(s => s.CriterionId)
            .ToList();

        var satisfied = state.CriterionStates.Where(s => s.Status == "satisfied").Select(s => s.CriterionId).ToList();
        var failed = state.CriterionStates.Where(s => s.Status == "failed").Select(s => s.CriterionId).ToList();
        var needsReview = state.CriterionStates.Where(s => s.Status == "needs_review").Select(s => s.CriterionId).ToList();
        var unresolved = state.CriterionStates.Where(s => s.Status == "unanswered").ToList();
        var skipped = unresolved.Select(s => s.CriterionId).ToList();

        var missingInformation = unresolved
            .Select(s => criteriaById.TryGetValue(s.CriterionId, out var c)
                ? $"{s.CriterionId}: {c.SimpleMeaning ?? c.OriginalText} — not answered before the session ended."
                : $"{s.CriterionId}: not answered before the session ended.")
            .ToList();

        var (summaryText, reasoning, nextAction, usedFallback, fallbackReason) =
            await BuildNarrativeAsync(recommendation, satisfied, failed, needsReview, missingInformation, criteriaById);

        var result = new SummaryResult(
            recommendation,
            summaryText,
            demographicSummary,
            criteriaSummary,
            criteriaCoveredByDemographics,
            satisfied,
            failed,
            needsReview,
            skipped,
            missingInformation,
            reasoning,
            nextAction,
            SummaryDisclaimer,
            usedFallback,
            fallbackReason);

        await PersistSummaryAsync(sessionId, result);
        await _db.SaveChangesAsync();

        return result;
    }

    private async Task PersistSummaryAsync(Guid sessionId, SummaryResult result)
    {
        var existing = await _db.ScreeningSummaries.FirstOrDefaultAsync(s => s.SessionId == sessionId);
        if (existing is not null)
        {
            _db.ScreeningSummaries.Remove(existing);
        }

        _db.ScreeningSummaries.Add(new ScreeningSummary
        {
            SessionId = sessionId,
            Recommendation = result.Recommendation,
            SummaryText = result.SummaryText,
            DemographicSummaryJson = JsonSerializer.Serialize(result.DemographicSummary),
            CriteriaSummaryJson = JsonSerializer.Serialize(result.CriteriaSummary),
            CriteriaCoveredByDemographicsJson = JsonSerializer.Serialize(result.CriteriaCoveredByDemographics),
            SatisfiedCriteriaJson = JsonSerializer.Serialize(result.SatisfiedCriteria),
            FailedCriteriaJson = JsonSerializer.Serialize(result.FailedCriteria),
            NeedsReviewCriteriaJson = JsonSerializer.Serialize(result.NeedsReviewCriteria),
            SkippedCriteriaJson = JsonSerializer.Serialize(result.SkippedCriteria),
            MissingInformationJson = JsonSerializer.Serialize(result.MissingInformation),
            ReasoningJson = JsonSerializer.Serialize(result.Reasoning),
            RecommendedNextAction = result.RecommendedNextAction,
            Disclaimer = result.Disclaimer,
            PromptVersion = SummaryPromptVersion,
            ModelName = result.UsedFallback ? FallbackModelLabel : _claudeService.Model
        });
    }

    private async Task<(string Summary, List<string> Reasoning, string NextAction, bool UsedFallback, string? FallbackReason)> BuildNarrativeAsync(
        string recommendation,
        List<string> satisfied,
        List<string> failed,
        List<string> needsReview,
        List<string> missingInformation,
        Dictionary<string, EligibilityCriterion> criteriaById)
    {
        var systemPrompt =
            "This is a clinical trial pre-screening assistant. Do not make final medical, diagnostic, or recruitment decisions. " +
            "Use only the provided criteria statuses. If information is missing or uncertain, say so. Return valid JSON where requested. " +
            "Keep wording simple and plain-language. The final decision remains with the recruiter/clinical reviewer.";

        var userPrompt =
            "Summarize this clinical trial pre-screening session for a recruiter and clinical reviewer. " +
            "Return valid JSON only, matching exactly this shape: " +
            "{ \"summary\": \"...\", \"reasoning\": [\"...\"], \"recommendedNextAction\": \"...\" }\n\n" +
            $"Overall recommendation: {recommendation}\n" +
            $"Satisfied criteria: {string.Join(", ", satisfied)}\n" +
            $"Failed criteria: {string.Join(", ", failed)}\n" +
            $"Needs-review criteria: {string.Join(", ", needsReview)}\n" +
            $"Missing/unanswered information: {string.Join("; ", missingInformation)}\n";

        var rawResponse = await _claudeService.SendAsync(systemPrompt, userPrompt);
        if (rawResponse is not null)
        {
            var parsed = TryParseNarrative(rawResponse);
            if (parsed is not null)
            {
                return (parsed.Value.Summary, parsed.Value.Reasoning, parsed.Value.NextAction, false, null);
            }

            _logger.LogError("Claude returned summary JSON that could not be parsed. Using rule-based summary.");
        }

        var fallback = BuildRuleBasedNarrative(recommendation, satisfied, failed, needsReview, missingInformation, criteriaById);
        return (fallback.Summary, fallback.Reasoning, fallback.NextAction, true, "Claude summary generation unavailable; used rule-based summary.");
    }

    private (string Summary, List<string> Reasoning, string NextAction)? TryParseNarrative(string rawResponse)
    {
        try
        {
            var json = ExtractJsonPayload(rawResponse);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("summary", out var summaryEl) || summaryEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var reasoning = new List<string>();
            if (root.TryGetProperty("reasoning", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in reasoningEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        reasoning.Add(item.GetString() ?? string.Empty);
                    }
                }
            }

            var nextAction = root.TryGetProperty("recommendedNextAction", out var nextActionEl) && nextActionEl.ValueKind == JsonValueKind.String
                ? nextActionEl.GetString() ?? string.Empty
                : string.Empty;

            return (summaryEl.GetString() ?? string.Empty, reasoning, nextAction);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Claude summary JSON.");
            return null;
        }
    }

    private static (string Summary, List<string> Reasoning, string NextAction) BuildRuleBasedNarrative(
        string recommendation,
        List<string> satisfied,
        List<string> failed,
        List<string> needsReview,
        List<string> missingInformation,
        Dictionary<string, EligibilityCriterion> criteriaById)
    {
        var reasoning = new List<string>();

        foreach (var id in failed)
        {
            var text = criteriaById.TryGetValue(id, out var c) ? c.SimpleMeaning ?? c.OriginalText : id;
            reasoning.Add($"{id} was not met: {text}");
        }

        foreach (var id in needsReview)
        {
            var text = criteriaById.TryGetValue(id, out var c) ? c.SimpleMeaning ?? c.OriginalText : id;
            reasoning.Add($"{id} needs clinical confirmation: {text}");
        }

        reasoning.AddRange(missingInformation);

        if (reasoning.Count == 0 && satisfied.Count > 0)
        {
            reasoning.Add("All assessed criteria were satisfied based on recruiter-recorded answers.");
        }

        string summary;
        string nextAction;

        switch (recommendation)
        {
            case "Likely Ineligible":
                summary = "Based on the recruiter's recorded answers, the patient does not appear to meet all eligibility requirements for this study.";
                nextAction = "Inform the patient they are unlikely to qualify at this time. Confirm with a clinical reviewer before final communication.";
                break;
            case "Needs Clinical Review":
                summary = "Based on the recruiter's recorded answers, one or more criteria are uncertain or incomplete and require clinical judgment.";
                nextAction = "Escalate to a clinical reviewer to confirm the uncertain or missing items before proceeding.";
                break;
            default:
                summary = "Based on the recruiter's recorded answers, the patient appears to meet all assessed eligibility criteria.";
                nextAction = "Proceed with clinical review and confirm final eligibility before enrollment.";
                break;
        }

        return (summary, reasoning, nextAction);
    }

    // Claude may wrap JSON in prose or code fences despite instructions; defensively
    // extract the outermost {...} block rather than assuming the response is pure JSON.
    private static string ExtractJsonPayload(string rawResponse)
    {
        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');
        return start >= 0 && end > start ? rawResponse[start..(end + 1)] : rawResponse;
    }

    private async Task<List<ScreeningQuestion>> LoadQuestionsAsync(Guid protocolId) =>
        await _db.ScreeningQuestions
            .Where(q => q.ProtocolId == protocolId)
            .OrderBy(q => q.IsDemographicQuestion ? 0 : 1)
            .ThenBy(q => q.DisplayOrder)
            .ToListAsync();

    private async Task<List<EligibilityCriterion>> LoadCriteriaAsync(Guid protocolId) =>
        await _db.EligibilityCriteria.Where(c => c.ProtocolId == protocolId).ToListAsync();

    private async Task<List<ScreeningAnswer>> LoadAnswersAsync(Guid sessionId) =>
        await _db.ScreeningAnswers.Where(a => a.SessionId == sessionId).ToListAsync();
}
