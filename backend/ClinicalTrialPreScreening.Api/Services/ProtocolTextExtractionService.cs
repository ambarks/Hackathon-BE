using ClinicalTrialPreScreening.Api.Utils;
using UglyToad.PdfPig;

namespace ClinicalTrialPreScreening.Api.Services;

public record ProtocolExtractionResult(string Text, bool UsedFallback, string? FallbackReason);

public class ProtocolTextExtractionService
{
    private readonly ILogger<ProtocolTextExtractionService> _logger;

    public ProtocolTextExtractionService(ILogger<ProtocolTextExtractionService> logger)
    {
        _logger = logger;
    }

    public async Task<ProtocolExtractionResult> ExtractFromPdfAsync(Stream pdfStream, string fileName)
    {
        try
        {
            using var document = PdfDocument.Open(pdfStream);
            var text = string.Join("\n\n", document.GetPages().Select(page => page.Text));

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("PDF {FileName} contained no extractable text. Falling back to sample protocol text.", fileName);
                return await BuildFallbackResultAsync("Uploaded PDF contained no extractable text.");
            }

            return new ProtocolExtractionResult(text, UsedFallback: false, FallbackReason: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF extraction failed for {FileName}. Falling back to sample protocol text.", fileName);
            return await BuildFallbackResultAsync("PDF parsing failed. Using sample protocol text instead.");
        }
    }

    public async Task<string> GetSampleProtocolTextAsync()
    {
        var path = SampleDataLocator.GetFilePath("sample-protocol.txt");
        return await File.ReadAllTextAsync(path);
    }

    private async Task<ProtocolExtractionResult> BuildFallbackResultAsync(string reason)
    {
        var sampleText = await GetSampleProtocolTextAsync();
        return new ProtocolExtractionResult(sampleText, UsedFallback: true, FallbackReason: reason);
    }
}
