using System.Text;
using System.Text.Json;

namespace ClinicalTrialPreScreening.Api.Services;

public record SessionExportData(
    Guid SessionId,
    string PatientAlias,
    string Status,
    string Recommendation,
    string Summary,
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
    string Disclaimer);

// Isolates JSON/CSV export formatting for a completed screening session summary.
public class ExportService
{
    public string BuildSummaryJson(SessionExportData data) =>
        JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    public string BuildSummaryCsv(SessionExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Section,Field,Value");

        AppendRow(sb, "Session", "SessionId", data.SessionId.ToString());
        AppendRow(sb, "Session", "PatientAlias", data.PatientAlias);
        AppendRow(sb, "Session", "Status", data.Status);
        AppendRow(sb, "Summary", "Recommendation", data.Recommendation);
        AppendRow(sb, "Summary", "Summary", data.Summary);
        AppendRow(sb, "Summary", "RecommendedNextAction", data.RecommendedNextAction);
        AppendRow(sb, "Summary", "Disclaimer", data.Disclaimer);

        foreach (var item in data.DemographicSummary)
        {
            AppendRow(sb, "Demographics", item.QuestionText, item.ResponseText);
        }

        foreach (var item in data.CriteriaSummary)
        {
            AppendRow(sb, "Criteria", $"{item.CriterionId} ({item.Type})", item.Status);
        }

        foreach (var reason in data.Reasoning)
        {
            AppendRow(sb, "Reasoning", "-", reason);
        }

        foreach (var missing in data.MissingInformation)
        {
            AppendRow(sb, "MissingInformation", "-", missing);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string section, string field, string value) =>
        sb.AppendLine($"{Csv(section)},{Csv(field)},{Csv(value)}");

    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
