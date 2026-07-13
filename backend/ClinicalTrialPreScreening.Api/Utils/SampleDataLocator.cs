namespace ClinicalTrialPreScreening.Api.Utils;

// Resolves paths into the repo's sample-data/ folder, which is mounted
// read-only into the api container at /app/sample-data (see docker-compose.yml).
public static class SampleDataLocator
{
    public static string GetFilePath(string fileName) => Path.Combine(ResolveSampleDataDir(), fileName);

    private static string ResolveSampleDataDir()
    {
        var configured = Environment.GetEnvironmentVariable("SAMPLE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        const string dockerDefault = "/app/sample-data";
        if (Directory.Exists(dockerDefault))
        {
            return dockerDefault;
        }

        // Local (non-Docker) dev fallback: repo-root/sample-data.
        return Path.Combine(AppContext.BaseDirectory, "sample-data");
    }
}
