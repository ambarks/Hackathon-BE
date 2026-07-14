using System.Text;
using ClinicalTrialPreScreening.Api.Data;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Services;
using ClinicalTrialPreScreening.Api.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalTrialPreScreening.Api.Controllers;

[ApiController]
[Route("api/screening-sessions")]
public class ScreeningSessionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ScreeningSessionService _screeningSessionService;
    private readonly ExportService _exportService;
    private readonly AuditService _auditService;

    public ScreeningSessionsController(
        AppDbContext db,
        ScreeningSessionService screeningSessionService,
        ExportService exportService,
        AuditService auditService)
    {
        _db = db;
        _screeningSessionService = screeningSessionService;
        _exportService = exportService;
        _auditService = auditService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PatientAlias))
        {
            return BadRequest(new { message = "patientAlias is required." });
        }

        var protocol = await _db.Protocols.FindAsync(request.ProtocolId);
        if (protocol is null)
        {
            return NotFound(new { message = $"Protocol {request.ProtocolId} was not found." });
        }

        var hasQuestions = await _db.ScreeningQuestions.AnyAsync(q => q.ProtocolId == request.ProtocolId);
        if (!hasQuestions)
        {
            return UnprocessableEntity(new { message = "This protocol has no generated question bank yet." });
        }

        var session = await _screeningSessionService.CreateSessionAsync(request.ProtocolId, request.PatientAlias);
        return Ok(ToSessionResponse(session));
    }

    [HttpGet("{sessionId:guid}")]
    public async Task<IActionResult> GetSession(Guid sessionId)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId);
        if (session is null)
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        return Ok(ToSessionResponse(session));
    }

    [HttpGet("{sessionId:guid}/next-question")]
    public async Task<IActionResult> GetNextQuestion(Guid sessionId)
    {
        if (!await SessionExistsAsync(sessionId))
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        var state = await _screeningSessionService.GetStateAsync(sessionId);
        var next = state.NextQuestion;

        if (next is null)
        {
            return Ok(new NextQuestionResponse(
                false, null, null, null, null, null, null, null, null, null, false, false, null,
                state.EarlyStopTriggered, state.EarlyStopReason, state.DisqualifyingCriterionId));
        }

        return Ok(new NextQuestionResponse(
            true,
            next.QuestionId,
            next.Section,
            next.QuestionText,
            next.AnswerType,
            JsonHelpers.DeserializeStringArray(next.OptionsJson),
            next.Priority,
            JsonHelpers.DeserializeStringArray(next.LinkedCriteriaJson),
            JsonHelpers.DeserializeStringArray(next.CoveredCriteriaJson),
            next.WhyAsked,
            next.IsDemographicQuestion,
            next.CanTriggerEarlyStop,
            next.EarlyStopReason,
            state.EarlyStopTriggered,
            state.EarlyStopReason,
            state.DisqualifyingCriterionId));
    }

    [HttpGet("{sessionId:guid}/current-section")]
    public async Task<IActionResult> GetCurrentSection(Guid sessionId)
    {
        if (!await SessionExistsAsync(sessionId))
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        var state = await _screeningSessionService.GetStateAsync(sessionId);
        return Ok(new { currentSection = state.CurrentSection });
    }

    [HttpGet("{sessionId:guid}/section-progress")]
    public async Task<IActionResult> GetSectionProgress(Guid sessionId)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId);
        if (session is null)
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        var questions = await _db.ScreeningQuestions.Where(q => q.ProtocolId == session.ProtocolId).ToListAsync();
        var answeredIds = await _db.ScreeningAnswers
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.QuestionId)
            .ToListAsync();
        var answeredSet = answeredIds.ToHashSet();

        var demoTotal = questions.Count(q => q.IsDemographicQuestion);
        var demoAnswered = questions.Count(q => q.IsDemographicQuestion && answeredSet.Contains(q.QuestionId));
        var critTotal = questions.Count(q => !q.IsDemographicQuestion);
        var critAnswered = questions.Count(q => !q.IsDemographicQuestion && answeredSet.Contains(q.QuestionId));

        var state = await _screeningSessionService.GetStateAsync(sessionId);

        return Ok(new SectionProgressResponse(
            new SectionProgressDetail(demoAnswered, demoTotal),
            new SectionProgressDetail(critAnswered, critTotal),
            state.CurrentSection,
            state.NextQuestion is null));
    }

    [HttpPost("{sessionId:guid}/answers")]
    public async Task<IActionResult> RecordAnswer(Guid sessionId, [FromBody] RecordAnswerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QuestionId) || request.Response is null)
        {
            return BadRequest(new { message = "questionId and response are required." });
        }

        if (!await SessionExistsAsync(sessionId))
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        try
        {
            var answer = await _screeningSessionService.RecordAnswerAsync(sessionId, request.QuestionId, request.Response);
            var session = await _db.ScreeningSessions.FindAsync(sessionId);

            await _auditService.LogEventAsync(
                "ScreeningAnswer",
                sessionId: sessionId,
                details: $"questionId={answer.QuestionId}; mappedStatus={answer.MappedEligibilityStatus}");

            return Ok(new AnswerResponse(
                answer.QuestionId,
                answer.Section,
                answer.ResponseText,
                answer.MappedEligibilityStatus,
                answer.CoveredMultipleCriteria,
                session!.CurrentSection,
                session.OverallLikelyStatus,
                session.Status == "Completed",
                session.EarlyStopRecommended,
                session.EarlyStopReason,
                session.DisqualifyingCriterionId));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // Recruiter-initiated: ends the session immediately, regardless of how many
    // questions remain unanswered. Never called automatically — the backend's
    // early-stop recommendation is always a suggestion, not a forced action
    // (see plan2.md "recommend, don't force"). Safe to call at any time, not
    // only when EarlyStopRecommended is true.
    [HttpPost("{sessionId:guid}/end")]
    public async Task<IActionResult> EndSession(Guid sessionId)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId);
        if (session is null)
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        if (session.Status != "Completed")
        {
            session.Status = "Completed";
            session.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _auditService.LogEventAsync(
                "ScreeningSessionEndedEarly",
                sessionId: sessionId,
                details: $"earlyStopRecommended={session.EarlyStopRecommended}; disqualifyingCriterionId={session.DisqualifyingCriterionId}");
        }

        return Ok(ToSessionResponse(session));
    }

    [HttpGet("{sessionId:guid}/summary")]
    public async Task<IActionResult> GetSummary(Guid sessionId)
    {
        if (!await SessionExistsAsync(sessionId))
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        try
        {
            var result = await _screeningSessionService.GenerateSummaryAsync(sessionId);

            await _auditService.LogEventAsync(
                "SummaryGenerated",
                sessionId: sessionId,
                details: $"recommendation={result.Recommendation}; usedFallback={result.UsedFallback}");

            return Ok(ToSummaryResponse(result));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpGet("{sessionId:guid}/export/json")]
    public async Task<IActionResult> ExportJson(Guid sessionId)
    {
        var exportData = await BuildExportDataAsync(sessionId);
        if (exportData is null)
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        var json = _exportService.BuildSummaryJson(exportData);
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"screening-summary-{sessionId}.json");
    }

    [HttpGet("{sessionId:guid}/export/csv")]
    public async Task<IActionResult> ExportCsv(Guid sessionId)
    {
        var exportData = await BuildExportDataAsync(sessionId);
        if (exportData is null)
        {
            return NotFound(new { message = $"Screening session {sessionId} was not found." });
        }

        var csv = _exportService.BuildSummaryCsv(exportData);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"screening-summary-{sessionId}.csv");
    }

    private async Task<SessionExportData?> BuildExportDataAsync(Guid sessionId)
    {
        var session = await _db.ScreeningSessions.FindAsync(sessionId);
        if (session is null)
        {
            return null;
        }

        var result = await _screeningSessionService.GenerateSummaryAsync(sessionId);

        return new SessionExportData(
            sessionId,
            session.PatientAlias,
            session.Status,
            result.Recommendation,
            result.SummaryText,
            result.DemographicSummary,
            result.CriteriaSummary,
            result.CriteriaCoveredByDemographics,
            result.SatisfiedCriteria,
            result.FailedCriteria,
            result.NeedsReviewCriteria,
            result.SkippedCriteria,
            result.NotApplicableCriteria,
            result.MissingInformation,
            result.Reasoning,
            result.RecommendedNextAction,
            result.Disclaimer);
    }

    private static SummaryResponse ToSummaryResponse(SummaryResult result) => new(
        result.Recommendation,
        result.SummaryText,
        result.DemographicSummary,
        result.CriteriaSummary,
        result.CriteriaCoveredByDemographics,
        result.SatisfiedCriteria,
        result.FailedCriteria,
        result.NeedsReviewCriteria,
        result.SkippedCriteria,
        result.NotApplicableCriteria,
        result.MissingInformation,
        result.Reasoning,
        result.RecommendedNextAction,
        result.Disclaimer,
        result.UsedFallback,
        result.FallbackReason);

    private async Task<bool> SessionExistsAsync(Guid sessionId) =>
        await _db.ScreeningSessions.AnyAsync(s => s.Id == sessionId);

    private static SessionResponse ToSessionResponse(ScreeningSession session) => new(
        session.Id,
        session.ProtocolId,
        session.PatientAlias,
        session.CurrentSection,
        session.Status,
        session.OverallLikelyStatus,
        session.CreatedAt,
        session.CompletedAt,
        session.EarlyStopRecommended,
        session.EarlyStopReason,
        session.DisqualifyingCriterionId);
}

public record CreateSessionRequest(Guid ProtocolId, string PatientAlias);

public record SessionResponse(
    Guid SessionId,
    Guid ProtocolId,
    string PatientAlias,
    string CurrentSection,
    string Status,
    string? OverallLikelyStatus,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    bool EarlyStopRecommended,
    string? EarlyStopReason,
    string? DisqualifyingCriterionId);

// CanTriggerEarlyStop/EarlyStopReason are static per-question metadata set at
// question-bank generation time (does this question *risk* disqualifying the
// patient). SessionEarlyStopRecommended/SessionEarlyStopReason/
// SessionDisqualifyingCriterionId are the live, backend-computed recommendation
// for the session *as of the answers recorded so far* (see plan2.md).
public record NextQuestionResponse(
    bool HasNextQuestion,
    string? QuestionId,
    string? Section,
    string? QuestionText,
    string? AnswerType,
    IReadOnlyList<string>? Options,
    string? Priority,
    IReadOnlyList<string>? LinkedCriteria,
    IReadOnlyList<string>? CoveredCriteria,
    string? WhyAsked,
    bool IsDemographicQuestion,
    bool CanTriggerEarlyStop,
    string? EarlyStopReason,
    bool SessionEarlyStopRecommended,
    string? SessionEarlyStopReason,
    string? SessionDisqualifyingCriterionId);

public record SectionProgressDetail(int Answered, int Total);

public record SectionProgressResponse(
    SectionProgressDetail Demographics,
    SectionProgressDetail Criteria,
    string CurrentSection,
    bool IsComplete);

public record RecordAnswerRequest(string QuestionId, string Response);

public record AnswerResponse(
    string QuestionId,
    string Section,
    string ResponseText,
    string? MappedEligibilityStatus,
    bool CoveredMultipleCriteria,
    string CurrentSection,
    string? OverallLikelyStatus,
    bool SessionCompleted,
    bool EarlyStopRecommended,
    string? EarlyStopReason,
    string? DisqualifyingCriterionId);

public record SummaryResponse(
    string Recommendation,
    string Summary,
    IReadOnlyList<DemographicAnswerSummary> DemographicSummary,
    IReadOnlyList<CriterionSummary> CriteriaSummary,
    IReadOnlyList<string> CriteriaCoveredByDemographics,
    IReadOnlyList<string> SatisfiedCriteria,
    IReadOnlyList<string> FailedCriteria,
    IReadOnlyList<string> NeedsReviewCriteria,
    IReadOnlyList<string> SkippedCriteria,
    IReadOnlyList<string> NotApplicableCriteria,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<string> Reasoning,
    string RecommendedNextAction,
    string Disclaimer,
    bool UsedFallback,
    string? FallbackReason);
