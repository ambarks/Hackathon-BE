using System.Text.Json;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Utils;

namespace ClinicalTrialPreScreening.Api.Services;

public record CriteriaExtractionResult(IReadOnlyList<EligibilityCriterion> Criteria, bool UsedFallback, string? FallbackReason);

public class CriteriaExtractionService
{
    private const string PromptVersion = "criteria-extraction-v1";
    private const string FallbackModelLabel = "sample-data-fallback";

    private readonly ClaudeService _claudeService;
    private readonly ILogger<CriteriaExtractionService> _logger;

    public CriteriaExtractionService(ClaudeService claudeService, ILogger<CriteriaExtractionService> logger)
    {
        _claudeService = claudeService;
        _logger = logger;
    }

    public async Task<CriteriaExtractionResult> ExtractAsync(Guid protocolId, string protocolText)
    {
        var rawResponse = await _claudeService.SendAsync(BuildSystemPrompt(), BuildUserPrompt(protocolText));

        if (rawResponse is not null)
        {
            var parsed = TryParseCriteria(rawResponse, protocolId, _claudeService.Model);
            if (parsed is not null && parsed.Count > 0)
            {
                return new CriteriaExtractionResult(parsed, UsedFallback: false, FallbackReason: null);
            }

            _logger.LogError("Claude returned criteria JSON that could not be parsed or was empty. Falling back to sample criteria.");
        }

        var fallback = await LoadSampleCriteriaAsync(protocolId);
        return new CriteriaExtractionResult(
            fallback,
            UsedFallback: true,
            FallbackReason: "Claude criteria extraction unavailable; used sample-data/sample-criteria.json.");
    }

    private static string BuildSystemPrompt() =>
        "This is a clinical trial pre-screening assistant. Do not make final medical, diagnostic, or recruitment decisions. " +
        "Use only the provided protocol text. If information is missing or uncertain, say so. Return valid JSON only where requested. " +
        "Keep patient-facing wording simple. The final decision remains with the recruiter/clinical reviewer.";

    private static string BuildUserPrompt(string protocolText) =>
        "Extract structured eligibility criteria from the following clinical trial protocol text. " +
        "Return valid JSON only, matching exactly this shape:\n" +
        "{ \"criteria\": [ { \"criterionId\": \"INC-001\", \"type\": \"Inclusion\", \"originalText\": \"...\", " +
        "\"simpleMeaning\": \"...\", \"patientQuestion\": \"...\", \"answerType\": \"yes_no|single_choice|free_text|date|number\", " +
        "\"options\": [], \"eligibilityImpact\": \"Required|Exclusionary|Needs Review\", \"priority\": \"High|Medium|Low\", " +
        "\"sourceSection\": \"...\", \"requiresClinicalReview\": false, \"canBeCoveredByDemographics\": false } ] }\n\n" +
        "Protocol text:\n" + protocolText;

    private List<EligibilityCriterion>? TryParseCriteria(string rawResponse, Guid protocolId, string modelName)
    {
        try
        {
            var json = ExtractJsonPayload(rawResponse);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("criteria", out var criteriaArray) ||
                criteriaArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<EligibilityCriterion>();
            foreach (var item in criteriaArray.EnumerateArray())
            {
                var criterion = MapCriterion(item, protocolId, modelName);
                if (criterion is not null)
                {
                    result.Add(criterion);
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Claude criteria extraction JSON.");
            return null;
        }
    }

    private static EligibilityCriterion? MapCriterion(JsonElement item, Guid protocolId, string modelName)
    {
        if (!item.TryGetProperty("criterionId", out var criterionIdEl) ||
            criterionIdEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? optionsJson = null;
        if (item.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
        {
            optionsJson = optionsEl.GetRawText();
        }

        return new EligibilityCriterion
        {
            ProtocolId = protocolId,
            CriterionId = criterionIdEl.GetString() ?? string.Empty,
            Type = GetStringOrDefault(item, "type", "Inclusion"),
            OriginalText = GetStringOrDefault(item, "originalText", string.Empty),
            SimpleMeaning = GetStringOrNull(item, "simpleMeaning"),
            PatientQuestion = GetStringOrNull(item, "patientQuestion"),
            AnswerType = GetStringOrDefault(item, "answerType", "yes_no"),
            OptionsJson = optionsJson,
            EligibilityImpact = GetStringOrDefault(item, "eligibilityImpact", "Required"),
            Priority = GetStringOrDefault(item, "priority", "Medium"),
            SourceSection = GetStringOrNull(item, "sourceSection"),
            RequiresClinicalReview = item.TryGetProperty("requiresClinicalReview", out var reviewEl) &&
                                     reviewEl.ValueKind == JsonValueKind.True,
            CanBeCoveredByDemographics = item.TryGetProperty("canBeCoveredByDemographics", out var demoEl) &&
                                         demoEl.ValueKind == JsonValueKind.True,
            IsApproved = false,
            PromptVersion = PromptVersion,
            ModelName = modelName
        };
    }

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

    private async Task<List<EligibilityCriterion>> LoadSampleCriteriaAsync(Guid protocolId)
    {
        var path = SampleDataLocator.GetFilePath("sample-criteria.json");
        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);

        var result = new List<EligibilityCriterion>();
        if (document.RootElement.TryGetProperty("criteria", out var criteriaArray) && criteriaArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in criteriaArray.EnumerateArray())
            {
                var criterion = MapCriterion(item, protocolId, FallbackModelLabel);
                if (criterion is not null)
                {
                    result.Add(criterion);
                }
            }
        }

        return result;
    }
}
