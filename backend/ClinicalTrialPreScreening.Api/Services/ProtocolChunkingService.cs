namespace ClinicalTrialPreScreening.Api.Services;

public record ProtocolChunkData(int ChunkIndex, string? SectionTitle, string ChunkText, int? PageNumber);

// Hackathon-simplified chunking: splits on blank lines and tracks the most
// recent heading-like line as the current section title.
public class ProtocolChunkingService
{
    public IReadOnlyList<ProtocolChunkData> ChunkText(string protocolText)
    {
        var paragraphs = protocolText
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var chunks = new List<ProtocolChunkData>();
        string? currentSection = null;
        var index = 0;

        foreach (var paragraph in paragraphs)
        {
            var firstLine = paragraph.Split('\n')[0].Trim();
            if (LooksLikeSectionHeading(firstLine))
            {
                currentSection = firstLine.TrimEnd(':');
            }

            chunks.Add(new ProtocolChunkData(index, currentSection, paragraph, PageNumber: null));
            index++;
        }

        return chunks;
    }

    private static bool LooksLikeSectionHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 80)
        {
            return false;
        }

        return line.EndsWith(':') || line == line.ToUpperInvariant();
    }
}
