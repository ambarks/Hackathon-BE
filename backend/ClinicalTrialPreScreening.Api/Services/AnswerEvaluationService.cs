using System.Text.Json;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Utils;

namespace ClinicalTrialPreScreening.Api.Services;

public record AnswerEvaluationResult(
    string MappedStatus, // satisfied | failed | needs_review
    string? Reasoning,
    string EvaluationSource, // "ai" | "fallback-rule"
    string PromptVersion,
    string ModelName);

// AI-assisted interpretation of a single screening answer against its linked
// criterion, used only for questions that can actually disqualify a patient
// (ScreeningQuestion.CanTriggerEarlyStop == true, i.e. linked to an Exclusion
// or Required-Inclusion criterion). Claude only classifies the answer text
// (satisfied/failed/needs_review) with reasoning; it never decides whether
// screening should stop — that is a fixed backend rule applied by
// ScreeningSessionService/AdaptiveQuestionService over this classification
// (see plan2.md, phases 14-15). Falls back to the existing deterministic
// AdaptiveQuestionService.DetermineMappedStatus heuristic whenever Claude is
// unavailable or returns an unparseable/invalid response, following the same
// defensive-parse pattern as every other ClaudeService caller in this project.
public class AnswerEvaluationService
{
    private const string PromptVersionValue = "answer-evaluation-v1";
    private const string FallbackModelLabel = "fallback-rule-based-evaluation";
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "satisfied", "failed", "needs_review"
    };

    private readonly ClaudeService _claudeService;
    private readonly ILogger<AnswerEvaluationService> _logger;

    public AnswerEvaluationService(ClaudeService claudeService, ILogger<AnswerEvaluationService> logger)
    {
        _claudeService = claudeService;
        _logger = logger;
    }

    public async Task<AnswerEvaluationResult> EvaluateAsync(
        ScreeningQuestion question,
        EligibilityCriterion criterion,
        string answerText)
    {
        var systemPrompt =
            "This is a clinical trial pre-screening assistant. Do not make final medical, diagnostic, or recruitment " +
            "decisions. Use only the criterion text and patient answer provided below — do not assume any information " +
            "that was not given. If the answer is ambiguous, uncertain, or does not clearly resolve the criterion, " +
            "classify it as needs_review rather than guessing. Return valid JSON only, matching exactly this shape: " +
            "{ \"status\": \"satisfied\" | \"failed\" | \"needs_review\", \"reasoning\": \"...\" }. " +
            "Keep the reasoning short, plain-language, and specific to the numbers/facts given. " +
            "The final eligibility decision remains with the recruiter/clinical reviewer.";

        var optionsText = JsonHelpers.DeserializeStringArray(criterion.OptionsJson) is { Count: > 0 } options
            ? string.Join(", ", options)
            : "n/a";

        var userPrompt =
            $"Criterion ID: {criterion.CriterionId}\n" +
            $"Criterion type: {criterion.Type} (eligibility impact: {criterion.EligibilityImpact})\n" +
            $"Criterion text: {criterion.OriginalText}\n" +
            $"Plain-language meaning: {criterion.SimpleMeaning ?? criterion.OriginalText}\n" +
            $"Expected answer format: {criterion.AnswerType} (options: {optionsText})\n" +
            $"Patient's recorded answer: \"{answerText}\"\n\n" +
            "Determine whether this answer satisfies, fails, or needs clinical review against the criterion above.";

        var rawResponse = await _claudeService.SendAsync(systemPrompt, userPrompt, maxTokens: 512);
        if (rawResponse is not null)
        {
            var parsed = TryParseEvaluation(rawResponse);
            if (parsed is not null)
            {
                return new AnswerEvaluationResult(parsed.Value.Status, parsed.Value.Reasoning, "ai", PromptVersionValue, _claudeService.Model);
            }

            _logger.LogError("Claude returned answer-evaluation JSON that could not be parsed or used an invalid status. Using rule-based fallback.");
        }

        var fallbackStatus = AdaptiveQuestionService.DetermineMappedStatus(criterion, answerText);
        return new AnswerEvaluationResult(fallbackStatus, null, "fallback-rule", PromptVersionValue, FallbackModelLabel);
    }

    private (string Status, string? Reasoning)? TryParseEvaluation(string rawResponse)
    {
        try
        {
            var json = ExtractJsonPayload(rawResponse);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var status = statusEl.GetString();
            if (status is null || !ValidStatuses.Contains(status))
            {
                return null;
            }

            var reasoning = root.TryGetProperty("reasoning", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String
                ? reasoningEl.GetString()
                : null;

            return (status.ToLowerInvariant(), reasoning);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Claude answer-evaluation JSON.");
            return null;
        }
    }

    // Claude may wrap JSON in prose or code fences despite instructions; defensively
    // extract the outermost {...} block rather than assuming the response is pure JSON.
    private static string ExtractJsonPayload(string rawResponse)
    {
        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');
        return start >= 0 && end > start ? rawResponse[start..(end + 1)] : rawResponse;
    }
}
