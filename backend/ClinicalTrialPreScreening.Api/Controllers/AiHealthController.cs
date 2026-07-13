using ClinicalTrialPreScreening.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalTrialPreScreening.Api.Controllers;

[ApiController]
[Route("api/health/ai")]
public class AiHealthController : ControllerBase
{
    private readonly AiStatusService _aiStatusService;

    public AiHealthController(AiStatusService aiStatusService)
    {
        _aiStatusService = aiStatusService;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var snapshot = _aiStatusService.GetSnapshot();

        return Ok(new
        {
            claudeConfigured = snapshot.ClaudeConfigured,
            model = snapshot.Model,
            lastCallSucceeded = snapshot.LastCallSucceeded,
            runningInFallbackMode = snapshot.RunningInFallbackMode,
            lastStatusMessage = snapshot.LastStatusMessage,
            lastCheckedAt = snapshot.LastCheckedAt
        });
    }
}
