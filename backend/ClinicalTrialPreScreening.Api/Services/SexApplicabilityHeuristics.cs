namespace ClinicalTrialPreScreening.Api.Services;

// Defensive backstop for EligibilityCriterion.AppliesToSex: never trust Claude's
// tagging alone. If Claude left a criterion untagged (null/"All") but its text
// clearly matches a curated list of biologically sex-specific terms, fill in
// the tag deterministically. This only ever fills a gap — it never overrides
// an explicit "Male"/"Female" tag Claude already returned, since Claude saw the
// full protocol context a keyword scan cannot. Mirrors the existing "never
// trust Claude's dedup claim alone" defensive re-check in
// QuestionBankService.RemoveDuplicateCriteriaQuestions.
public static class SexApplicabilityHeuristics
{
    private static readonly string[] FemaleTerms =
    {
        "pregnan", "breastfeed", "breast-feed", "lactat", "menstrua", "ovarian", "ovary",
        "uterus", "uterine", "cervical", "cervix", "endometri"
    };

    private static readonly string[] MaleTerms =
    {
        "prostate", "testicular", "testes", "testis", "semen", "ejaculat", "erectile"
    };

    public static string? Apply(string? explicitAppliesToSex, string originalText, string? simpleMeaning, string? patientQuestion)
    {
        var normalizedExplicit = Normalize(explicitAppliesToSex);
        if (normalizedExplicit is not null)
        {
            // Claude already made an explicit call — trust it over a keyword scan.
            return normalizedExplicit;
        }

        var haystack = string.Join(" ", new[] { originalText, simpleMeaning, patientQuestion })
            .ToLowerInvariant();

        var matchesFemale = FemaleTerms.Any(term => haystack.Contains(term, StringComparison.Ordinal));
        var matchesMale = MaleTerms.Any(term => haystack.Contains(term, StringComparison.Ordinal));

        // Ambiguous (both or neither list matched) — leave untagged rather than guess.
        if (matchesFemale == matchesMale)
        {
            return null;
        }

        return matchesFemale ? "Female" : "Male";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("All", StringComparison.OrdinalIgnoreCase) ? null : value;
}
