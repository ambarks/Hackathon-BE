namespace ClinicalTrialPreScreening.Api.Models;

public class Protocol
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? FileName { get; set; }
    public string Source { get; set; } = "sample"; // "upload" | "sample"
    public string RawText { get; set; } = string.Empty;
    public string Status { get; set; } = "Uploaded"; // Uploaded | CriteriaExtracted | QuestionsGenerated
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
