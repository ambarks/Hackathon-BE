using ClinicalTrialPreScreening.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalTrialPreScreening.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<HealthController> _logger;

    public HealthController(AppDbContext db, ILogger<HealthController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetHealth() => Ok(new { status = "ok" });

    [HttpGet("database")]
    public async Task<IActionResult> GetDatabaseHealth()
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync();
            if (!canConnect)
            {
                return StatusCode(503, new { status = "unhealthy", message = "Cannot connect to SQL Server." });
            }

            return Ok(new { status = "healthy" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed.");
            return StatusCode(503, new { status = "unhealthy", message = "Database health check failed. See server logs for details." });
        }
    }
}
