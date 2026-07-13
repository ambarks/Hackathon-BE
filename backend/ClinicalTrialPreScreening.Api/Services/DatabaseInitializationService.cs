using ClinicalTrialPreScreening.Api.Data;

namespace ClinicalTrialPreScreening.Api.Services;

// Runs DbInitializer.EnsureSchemaAsync with retry/backoff at startup so the
// api container tolerates SQL Server's Docker startup delay instead of
// failing permanently. Never throws out of StartAsync: a final failure is
// logged and left for GET /api/health/database to surface.
public class DatabaseInitializationService : IHostedService
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializationService> _logger;

    public DatabaseInitializationService(IServiceScopeFactory scopeFactory, ILogger<DatabaseInitializationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                await DbInitializer.EnsureSchemaAsync(db, _logger);
                _logger.LogInformation("Database schema is ready after {Attempt} attempt(s).", attempt);
                return;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "SQL Server not ready yet (attempt {Attempt}/{MaxAttempts}). Retrying in {DelaySeconds}s.",
                    attempt, MaxAttempts, RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        _logger.LogError(
            "Database schema could not be initialized after {MaxAttempts} attempts. " +
            "The API will keep running; /api/health/database will report unhealthy until SQL Server is reachable.",
            MaxAttempts);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
