using ClinicalTrialPreScreening.Api.Data;
using ClinicalTrialPreScreening.Api.Models;

namespace ClinicalTrialPreScreening.Api.Services;

// Records the responsible-AI audit trail (prompt.txt Security and Responsible
// AI section): protocol upload, criteria extraction, criteria approval,
// question bank generation, question de-duplication, screening answers, and
// generated summaries. Never throws — a logging failure must not break the
// primary request flow.
public class AuditService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppDbContext db, ILogger<AuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogEventAsync(string eventType, Guid? protocolId = null, Guid? sessionId = null, string? details = null)
    {
        try
        {
            _db.AuditEvents.Add(new AuditEvent
            {
                EventType = eventType,
                ProtocolId = protocolId,
                SessionId = sessionId,
                Details = details
            });

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit event {EventType}.", eventType);
        }
    }
}
