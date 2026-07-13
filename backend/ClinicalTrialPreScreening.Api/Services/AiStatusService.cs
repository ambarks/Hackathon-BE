namespace ClinicalTrialPreScreening.Api.Services;

public record AiStatusSnapshot(
    bool ClaudeConfigured,
    string Model,
    bool LastCallSucceeded,
    bool RunningInFallbackMode,
    string? LastError,
    string LastStatusMessage,
    DateTime LastCheckedAt);

// Singleton so the whole app shares one view of Claude configuration/fallback
// state; exposed via GET /api/health/ai.
public class AiStatusService
{
    private readonly object _lock = new();

    private bool _claudeConfigured;
    private string _model = "claude-3-5-sonnet-latest";
    private bool _lastCallSucceeded;
    private bool _runningInFallbackMode = true;
    private string? _lastError;
    private string _lastStatusMessage = "Claude has not been called yet.";
    private DateTime _lastCheckedAt = DateTime.UtcNow;

    public void SetConfigured(bool configured, string model)
    {
        lock (_lock)
        {
            _claudeConfigured = configured;
            _model = model;

            if (!configured)
            {
                _runningInFallbackMode = true;
                _lastStatusMessage = "Claude API key is not configured. Running in local fallback demo mode.";
                _lastCheckedAt = DateTime.UtcNow;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _lastCallSucceeded = true;
            _runningInFallbackMode = false;
            _lastError = null;
            _lastStatusMessage = "Claude API call succeeded.";
            _lastCheckedAt = DateTime.UtcNow;
        }
    }

    public void RecordFallback(string statusMessage)
    {
        lock (_lock)
        {
            _lastCallSucceeded = false;
            _runningInFallbackMode = true;
            _lastError = statusMessage;
            _lastStatusMessage = statusMessage;
            _lastCheckedAt = DateTime.UtcNow;
        }
    }

    public AiStatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new AiStatusSnapshot(
                _claudeConfigured,
                _model,
                _lastCallSucceeded,
                _runningInFallbackMode,
                _lastError,
                _lastStatusMessage,
                _lastCheckedAt);
        }
    }
}
