using System.Text.Json;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Utils;

namespace ClinicalTrialPreScreening.Api.Services;

public record SuppressedDuplicateCriterion(string CriterionId, string CoveredByQuestionId, string Reason);

public record QuestionBankResult(
    IReadOnlyList<ScreeningQuestion> Questions,
    IReadOnlyList<SuppressedDuplicateCriterion> SuppressedDuplicateCriteria,
    string SequencingRationale,
    bool UsedFallback,
    string? FallbackReason);

// Generates the two-section (Demographics + Criteria) question bank. Claude is
// asked to produce both the questions and a suggested mixed sequencing; if it
// fails or returns something invalid, a deterministic backend sequencing
// algorithm takes over so the flow never breaks (prompt.txt sections D/G).
public class QuestionBankService
{
    private const string PromptVersion = "question-bank-v1";
    private const string FallbackModelLabel = "fallback-deterministic-sequencing";
    private const int MaxTotalQuestions = 18;

    private readonly ClaudeService _claudeService;
    private readonly ILogger<QuestionBankService> _logger;

    public QuestionBankService(ClaudeService claudeService, ILogger<QuestionBankService> logger)
    {
        _claudeService = claudeService;
        _logger = logger;
    }

    public async Task<QuestionBankResult> GenerateAsync(Guid protocolId, IReadOnlyList<EligibilityCriterion> approvedCriteria)
    {
        // Question bank JSON is verbose per question (text, whyAsked, linkedCriteria, etc.);
        // the default 4096-token cap truncates it for protocols with many approved criteria,
        // same failure mode as criteria extraction, so give it the same headroom.
        var rawResponse = await _claudeService.SendAsync(BuildSystemPrompt(), BuildUserPrompt(approvedCriteria), maxTokens: 16000);

        if (rawResponse is not null)
        {
            var parsed = TryParseQuestionBank(rawResponse, protocolId, _claudeService.Model);
            if (parsed is not null)
            {
                return parsed;
            }

            _logger.LogError("Claude returned question bank JSON that could not be parsed or was invalid. Using deterministic fallback sequencing.");
        }

        return BuildDeterministicFallback(protocolId, approvedCriteria);
    }

    private static string BuildSystemPrompt() =>
        "This is a clinical trial pre-screening assistant. Do not make final medical, diagnostic, or recruitment decisions. " +
        "Use only the provided eligibility criteria. If information is missing or uncertain, say so. Return valid JSON where requested. " +
        "Keep patient-facing wording simple. The final decision remains with the recruiter/clinical reviewer. " +
        "Generate exactly two question sections: Demographics and Criteria. Do not generate separate Inclusion and Exclusion sections. " +
        $"Sequence the Criteria section so exclusion criteria (fast disqualifiers) come before inclusion criteria, prioritizing the " +
        "highest-impact/highest-priority questions first within each group, so a patient's ineligibility can be identified as early " +
        $"as possible. The total number of questions across both sections combined must not exceed {MaxTotalQuestions}; if there are " +
        "more approved criteria than that, choose the most decision-critical ones (high-impact exclusions and essential high-priority " +
        "inclusions) and omit lower-priority or redundant criteria rather than truncating arbitrarily. " +
        "Avoid duplicate questions if a demographic answer already covers the criterion.";

    private static string BuildUserPrompt(IReadOnlyList<EligibilityCriterion> criteria)
    {
        var criteriaJson = JsonSerializer.Serialize(criteria.Select(c => new
        {
            criterionId = c.CriterionId,
            type = c.Type,
            originalText = c.OriginalText,
            simpleMeaning = c.SimpleMeaning,
            patientQuestion = c.PatientQuestion,
            answerType = c.AnswerType,
            options = JsonHelpers.DeserializeStringArray(c.OptionsJson),
            eligibilityImpact = c.EligibilityImpact,
            priority = c.Priority,
            requiresClinicalReview = c.RequiresClinicalReview,
            canBeCoveredByDemographics = c.CanBeCoveredByDemographics
        }));

        return
            "Generate a sectioned question bank from the approved eligibility criteria below.\n\n" +
            "Sections:\n1. Demographics\n2. Criteria\n\n" +
            "Rules:\n" +
            "- Put age, pregnancy status when relevant, sex-at-birth when explicitly required, visit availability, consent capability, " +
            "and other patient profile/logistical questions under Demographics.\n" +
            "- If a demographic question covers an inclusion or exclusion criterion, do not generate another duplicate question under Criteria.\n" +
            "- Return linkedCriteria for each question.\n" +
            "- Return coveredCriteria for demographic questions.\n" +
            "- Return suppressedDuplicateCriteria for criteria that should not be asked separately.\n" +
            "- For the Criteria section, ask exclusion criteria before inclusion criteria so a disqualifying answer surfaces as early " +
            "as possible; within exclusion criteria and within inclusion criteria, ask the highest-priority/highest-impact ones first.\n" +
            $"- Keep the combined Demographics + Criteria question count at or below {MaxTotalQuestions}. If more approved criteria " +
            "exist than fit, keep the highest-impact exclusion criteria and the essential high-priority inclusion criteria, and omit " +
            "the rest — never drop a high-impact disqualifying criterion to keep a lower-priority one.\n" +
            "- Keep patient-facing questions short, simple, and conversational.\n" +
            "- Return valid JSON only, matching exactly this shape:\n" +
            "{ \"sections\": [ { \"section\": \"Demographics\", \"displayOrder\": 1, \"questions\": [ { \"questionId\": \"DEM-001\", " +
            "\"questionText\": \"...\", \"answerType\": \"yes_no|single_choice|free_text|date|number\", \"options\": [], " +
            "\"priority\": \"High|Medium|Low\", \"displayOrder\": 1, \"linkedCriteria\": [], \"coveredCriteria\": [], \"whyAsked\": \"...\", " +
            "\"isDemographicQuestion\": true, \"isDuplicateSuppressed\": false, \"canTriggerEarlyStop\": false, \"earlyStopReason\": \"\" } ] }, " +
            "{ \"section\": \"Criteria\", \"displayOrder\": 2, \"questions\": [ /* same shape, isDemographicQuestion: false */ ] } ], " +
            "\"suppressedDuplicateCriteria\": [ { \"criterionId\": \"...\", \"coveredByQuestionId\": \"...\", \"reason\": \"...\" } ], " +
            "\"sequencingRationale\": \"...\" }\n\n" +
            "Approved eligibility criteria (JSON):\n" + criteriaJson;
    }

    private QuestionBankResult? TryParseQuestionBank(string rawResponse, Guid protocolId, string modelName)
    {
        try
        {
            var json = ExtractJsonPayload(rawResponse);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("sections", out var sectionsEl) || sectionsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var demographicsQuestions = new List<ScreeningQuestion>();
            var criteriaQuestions = new List<ScreeningQuestion>();

            foreach (var sectionEl in sectionsEl.EnumerateArray())
            {
                if (!sectionEl.TryGetProperty("section", out var sectionNameEl) || sectionNameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var sectionName = sectionNameEl.GetString() ?? string.Empty;
                var targetList = sectionName.Equals("Demographics", StringComparison.OrdinalIgnoreCase) ? demographicsQuestions
                    : sectionName.Equals("Criteria", StringComparison.OrdinalIgnoreCase) ? criteriaQuestions
                    : null;

                // Reject anything that isn't exactly Demographics or Criteria (no Inclusion/Exclusion sections allowed).
                if (targetList is null || !sectionEl.TryGetProperty("questions", out var questionsEl) || questionsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in questionsEl.EnumerateArray())
                {
                    var question = MapQuestion(item, protocolId, sectionName, modelName);
                    if (question is not null)
                    {
                        targetList.Add(question);
                    }
                }
            }

            if (demographicsQuestions.Count == 0 && criteriaQuestions.Count == 0)
            {
                return null;
            }

            var suppressed = new List<SuppressedDuplicateCriterion>();
            if (root.TryGetProperty("suppressedDuplicateCriteria", out var suppressedEl) && suppressedEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in suppressedEl.EnumerateArray())
                {
                    if (item.TryGetProperty("criterionId", out var cid) && cid.ValueKind == JsonValueKind.String)
                    {
                        suppressed.Add(new SuppressedDuplicateCriterion(
                            cid.GetString() ?? string.Empty,
                            GetStringOrDefault(item, "coveredByQuestionId", string.Empty),
                            GetStringOrDefault(item, "reason", string.Empty)));
                    }
                }
            }

            var rationale = GetStringOrDefault(root, "sequencingRationale",
                "Demographics are asked first. Criteria questions are then ordered to ask high-impact disqualifying or essential eligibility questions early.");

            // Defensive re-check: never trust Claude's dedup claim alone. Any Criteria
            // question whose linked criterion is already covered by a demographic
            // question is dropped, regardless of what Claude marked.
            var (dedupedCriteriaQuestions, additionalSuppressed) = RemoveDuplicateCriteriaQuestions(demographicsQuestions, criteriaQuestions);
            suppressed.AddRange(additionalSuppressed);

            var allQuestions = ApplyMaxQuestionCap(demographicsQuestions, dedupedCriteriaQuestions, ref rationale);

            return new QuestionBankResult(allQuestions, suppressed, rationale, UsedFallback: false, FallbackReason: null);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Claude question bank JSON.");
            return null;
        }
    }

    private static ScreeningQuestion? MapQuestion(JsonElement item, Guid protocolId, string sectionName, string modelName)
    {
        if (!item.TryGetProperty("questionId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var isDemographic = sectionName.Equals("Demographics", StringComparison.OrdinalIgnoreCase);

        string? optionsJson = item.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array
            ? optionsEl.GetRawText()
            : null;

        var linkedCriteriaJson = item.TryGetProperty("linkedCriteria", out var linkedEl) && linkedEl.ValueKind == JsonValueKind.Array
            ? linkedEl.GetRawText()
            : "[]";

        var coveredCriteriaJson = item.TryGetProperty("coveredCriteria", out var coveredEl) && coveredEl.ValueKind == JsonValueKind.Array
            ? coveredEl.GetRawText()
            : "[]";

        var sourceCriteriaText = GetStringOrNull(item, "sourceCriteriaText");

        // Defensive: Claude is instructed to always return whyAsked, but if it's
        // missing or blank, synthesize a rule-based explanation rather than
        // persisting an empty field the UI would have nothing to show for.
        var whyAsked = GetStringOrNull(item, "whyAsked");
        if (string.IsNullOrWhiteSpace(whyAsked))
        {
            whyAsked = BuildDefaultWhyAsked(JsonHelpers.DeserializeStringArray(linkedCriteriaJson), sourceCriteriaText, isDemographic);
        }

        return new ScreeningQuestion
        {
            ProtocolId = protocolId,
            QuestionId = idEl.GetString() ?? string.Empty,
            Section = isDemographic ? "Demographics" : "Criteria",
            QuestionText = GetStringOrDefault(item, "questionText", string.Empty),
            AnswerType = GetStringOrDefault(item, "answerType", "yes_no"),
            OptionsJson = optionsJson,
            Priority = GetStringOrDefault(item, "priority", "Medium"),
            DisplayOrder = item.TryGetProperty("displayOrder", out var orderEl) && orderEl.TryGetInt32(out var order) ? order : 0,
            LinkedCriteriaJson = linkedCriteriaJson,
            CoveredCriteriaJson = coveredCriteriaJson,
            SourceCriteriaText = sourceCriteriaText,
            WhyAsked = whyAsked,
            IsDemographicQuestion = isDemographic,
            IsDuplicateSuppressed = false,
            CanTriggerEarlyStop = item.TryGetProperty("canTriggerEarlyStop", out var earlyEl) && earlyEl.ValueKind == JsonValueKind.True,
            EarlyStopReason = GetStringOrNull(item, "earlyStopReason"),
            PromptVersion = PromptVersion,
            ModelName = modelName
        };
    }

    private static string BuildDefaultWhyAsked(IReadOnlyList<string> linkedCriteria, string? sourceCriteriaText, bool isDemographic)
    {
        var criteriaList = linkedCriteria.Count > 0 ? string.Join(", ", linkedCriteria) : "the relevant eligibility criteria";
        var context = !string.IsNullOrWhiteSpace(sourceCriteriaText) ? $": {sourceCriteriaText}" : ".";

        return isDemographic
            ? $"This demographic question helps determine eligibility for {criteriaList}{context}"
            : $"This question checks eligibility criterion {criteriaList}{context}";
    }

    private static (List<ScreeningQuestion> Filtered, List<SuppressedDuplicateCriterion> Suppressed) RemoveDuplicateCriteriaQuestions(
        List<ScreeningQuestion> demographicsQuestions, List<ScreeningQuestion> criteriaQuestions)
    {
        var coveredByQuestion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var demoQuestion in demographicsQuestions)
        {
            foreach (var criterionId in JsonHelpers.DeserializeStringArray(demoQuestion.CoveredCriteriaJson))
            {
                coveredByQuestion.TryAdd(criterionId, demoQuestion.QuestionId);
            }
        }

        var filtered = new List<ScreeningQuestion>();
        var suppressed = new List<SuppressedDuplicateCriterion>();

        foreach (var question in criteriaQuestions)
        {
            var linkedCriteria = JsonHelpers.DeserializeStringArray(question.LinkedCriteriaJson);
            var duplicateCriterion = linkedCriteria.FirstOrDefault(c => coveredByQuestion.ContainsKey(c));

            if (duplicateCriterion is not null)
            {
                suppressed.Add(new SuppressedDuplicateCriterion(
                    duplicateCriterion,
                    coveredByQuestion[duplicateCriterion],
                    $"{duplicateCriterion} was already covered by demographic question {coveredByQuestion[duplicateCriterion]}."));
                continue;
            }

            filtered.Add(question);
        }

        return (filtered, suppressed);
    }

    private QuestionBankResult BuildDeterministicFallback(Guid protocolId, IReadOnlyList<EligibilityCriterion> approvedCriteria)
    {
        var demographicsQuestions = new List<ScreeningQuestion>();
        var criteriaQuestions = new List<ScreeningQuestion>();
        var suppressed = new List<SuppressedDuplicateCriterion>();

        // approvedCriteria has no guaranteed order (no ORDER BY on the DB query
        // that produced it), so both groups are explicitly sorted here to make
        // this fallback's numbering (DEM-001, CRT-001, ...) actually deterministic
        // across runs rather than following whatever arbitrary order SQL Server returned.
        // Exclusionary-impact demographic questions (quick disqualifiers, e.g. pregnancy,
        // age cutoffs) are asked before Required ones for the same reason Criteria
        // sequencing puts exclusion first: they can end screening earliest.
        var demographicCriteria = approvedCriteria
            .Where(c => c.CanBeCoveredByDemographics)
            .OrderByDescending(c => c.EligibilityImpact.Equals("Exclusionary", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(c => PriorityRank(c.Priority))
            .ThenBy(c => c.CriterionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var remainingCriteria = approvedCriteria.Where(c => !c.CanBeCoveredByDemographics).ToList();

        var demoIndex = 1;
        foreach (var criterion in demographicCriteria)
        {
            var questionId = $"DEM-{demoIndex:000}";
            var isExclusionary = criterion.EligibilityImpact.Equals("Exclusionary", StringComparison.OrdinalIgnoreCase);

            var question = new ScreeningQuestion
            {
                ProtocolId = protocolId,
                QuestionId = questionId,
                Section = "Demographics",
                QuestionText = criterion.PatientQuestion ?? criterion.SimpleMeaning ?? criterion.OriginalText,
                AnswerType = criterion.AnswerType,
                OptionsJson = criterion.OptionsJson,
                Priority = criterion.Priority,
                DisplayOrder = demoIndex,
                LinkedCriteriaJson = JsonSerializer.Serialize(new[] { criterion.CriterionId }),
                CoveredCriteriaJson = JsonSerializer.Serialize(new[] { criterion.CriterionId }),
                SourceCriteriaText = criterion.OriginalText,
                WhyAsked = $"This demographic question determines eligibility for criterion {criterion.CriterionId}: {criterion.SimpleMeaning ?? criterion.OriginalText}",
                IsDemographicQuestion = true,
                IsDuplicateSuppressed = false,
                CanTriggerEarlyStop = isExclusionary,
                EarlyStopReason = isExclusionary
                    ? $"An answer indicating this exclusion criterion applies may mean the patient is likely ineligible ({criterion.CriterionId})."
                    : null,
                PromptVersion = PromptVersion,
                ModelName = FallbackModelLabel
            };

            demographicsQuestions.Add(question);

            suppressed.Add(new SuppressedDuplicateCriterion(
                criterion.CriterionId,
                questionId,
                $"{criterion.CriterionId} is covered by demographic question {questionId} and is not asked again in the Criteria section."));

            demoIndex++;
        }

        // Deterministic mixed sequencing: exclusion criteria are asked before inclusion
        // criteria (a disqualifying answer ends screening fastest), and within each
        // group the highest-priority/highest-impact criteria are asked first. This is
        // also what determines which criteria survive the MaxTotalQuestions cap below,
        // since it's applied by taking the front of this ordered list.
        var orderedCriteria = remainingCriteria
            .OrderByDescending(c => c.Type.Equals("Exclusion", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(c => PriorityRank(c.Priority))
            .ThenBy(c => c.CriterionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var criteriaIndex = 1;
        foreach (var criterion in orderedCriteria)
        {
            var isExclusion = criterion.Type.Equals("Exclusion", StringComparison.OrdinalIgnoreCase);

            criteriaQuestions.Add(new ScreeningQuestion
            {
                ProtocolId = protocolId,
                QuestionId = $"CRT-{criteriaIndex:000}",
                Section = "Criteria",
                QuestionText = criterion.PatientQuestion ?? criterion.SimpleMeaning ?? criterion.OriginalText,
                AnswerType = criterion.AnswerType,
                OptionsJson = criterion.OptionsJson,
                Priority = criterion.Priority,
                DisplayOrder = criteriaIndex,
                LinkedCriteriaJson = JsonSerializer.Serialize(new[] { criterion.CriterionId }),
                CoveredCriteriaJson = "[]",
                SourceCriteriaText = criterion.OriginalText,
                WhyAsked = $"This question checks {criterion.Type.ToLowerInvariant()} criterion {criterion.CriterionId}: {criterion.SimpleMeaning ?? criterion.OriginalText}",
                IsDemographicQuestion = false,
                IsDuplicateSuppressed = false,
                CanTriggerEarlyStop = isExclusion,
                EarlyStopReason = isExclusion
                    ? $"A disqualifying answer here may indicate likely ineligibility due to exclusion criterion {criterion.CriterionId}."
                    : null,
                PromptVersion = PromptVersion,
                ModelName = FallbackModelLabel
            });

            criteriaIndex++;
        }

        var rationale =
            "Deterministic backend fallback sequencing: criteria markable as demographic questions are asked first as part of " +
            "Demographics; remaining criteria are then ordered by priority (High to Low), with exclusion criteria placed before " +
            "inclusion criteria within the same priority tier so high-impact disqualifying questions are asked early.";

        var allQuestions = ApplyMaxQuestionCap(demographicsQuestions, criteriaQuestions, ref rationale);

        return new QuestionBankResult(
            allQuestions,
            suppressed,
            rationale,
            UsedFallback: true,
            FallbackReason: "Claude question bank generation unavailable; used deterministic backend sequencing.");
    }

    // Caps the total patient-facing question count regardless of source (Claude or
    // deterministic fallback). Demographics questions are kept in full (they're few
    // and always asked first); Criteria questions are truncated to the remaining
    // budget, preserving whatever priority/early-stop-first order was already
    // computed upstream so the highest-value questions are the ones kept.
    private static List<ScreeningQuestion> ApplyMaxQuestionCap(
        List<ScreeningQuestion> demographicsQuestions,
        List<ScreeningQuestion> criteriaQuestions,
        ref string rationale)
    {
        var totalBeforeCap = demographicsQuestions.Count + criteriaQuestions.Count;
        if (totalBeforeCap <= MaxTotalQuestions)
        {
            return demographicsQuestions.Concat(criteriaQuestions).ToList();
        }

        var cappedDemographics = demographicsQuestions.Take(MaxTotalQuestions).ToList();
        var remainingBudget = Math.Max(0, MaxTotalQuestions - cappedDemographics.Count);
        var cappedCriteria = criteriaQuestions.Take(remainingBudget).ToList();

        rationale += $" Question count capped at {MaxTotalQuestions} (from {totalBeforeCap}); " +
            "lower-priority Criteria questions beyond the cap were dropped.";

        return cappedDemographics.Concat(cappedCriteria).ToList();
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0
    };

    private static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;

    private static string? GetStringOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Claude may wrap JSON in prose or code fences despite instructions; defensively
    // extract the outermost {...} block rather than assuming the response is pure JSON.
    private static string ExtractJsonPayload(string rawResponse)
    {
        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');
        return start >= 0 && end > start ? rawResponse[start..(end + 1)] : rawResponse;
    }
}
