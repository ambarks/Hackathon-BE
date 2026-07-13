using Microsoft.EntityFrameworkCore;

namespace ClinicalTrialPreScreening.Api.Data;

public static class DbInitializer
{
    public static async Task EnsureSchemaAsync(AppDbContext db, ILogger logger)
    {
        try
        {
            logger.LogInformation("Ensuring database and schema are created for hackathon POC.");
            await db.Database.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database schema initialization failed.");
            throw;
        }
    }
}
