using System.Text.Json;
using ClinicalTrialPreScreening.Api.Data;
using ClinicalTrialPreScreening.Api.Models;
using ClinicalTrialPreScreening.Api.Services;
using ClinicalTrialPreScreening.Api.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalTrialPreScreening.Api.Controllers;

[ApiController]
[Route("api")]
public class CriteriaController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CriteriaExtractionService _criteriaExtractionService;
    private readonly QuestionBankService _questionBankService;
    private readonly AuditService _auditService;

    public CriteriaController(
        AppDbContext db,
        CriteriaExtractionService criteriaExtractionService,
        QuestionBankService questionBankService,
        AuditService auditService)
    {
        _db = db;
        _criteriaExtractionService = criteriaExtractionService;
        _questionBankService = questionBankService;
        _auditService = auditService;
    }

    [HttpPost("protocols/{protocolId:guid}/extract-criteria")]
    public async Task<IActionResult> ExtractCriteria(Guid protocolId)
    {
        var protocol = await _db.Protocols.FindAsync(protocolId);
        if (protocol is null)
        {
            return NotFound(new { message = $"Protocol {protocolId} was not found." });
        }

        if (string.IsNullOrWhiteSpace(protocol.RawText))
        {
            return UnprocessableEntity(new { message = "Protocol has no extracted text to analyze." });
        }

        // Re-extraction replaces any previously extracted criteria for this protocol.
        var existing = _db.EligibilityCriteria.Where(c => c.ProtocolId == protocolId);
        _db.EligibilityCriteria.RemoveRange(existing);

        var result = await _criteriaExtractionService.ExtractAsync(protocolId, protocol.RawText);
        await _db.EligibilityCriteria.AddRangeAsync(result.Criteria);

        protocol.Status = "CriteriaExtracted";
        await _db.SaveChangesAsync();

        await _auditService.LogEventAsync(
            "CriteriaExtraction",
            protocolId: protocolId,
            details: $"usedFallback={result.UsedFallback}; count={result.Criteria.Count}");

        return Ok(new
        {
            protocolId,
            usedFallback = result.UsedFallback,
            fallbackReason = result.FallbackReason,
            criteria = result.Criteria.Select(ToCriterionResponse)
        });
    }

    [HttpGet("protocols/{protocolId:guid}/criteria")]
    public async Task<IActionResult> GetCriteria(Guid protocolId)
    {
        var criteria = await _db.EligibilityCriteria
            .Where(c => c.ProtocolId == protocolId)
            .OrderBy(c => c.CriterionId)
            .ToListAsync();

        return Ok(criteria.Select(ToCriterionResponse));
    }

    [HttpPut("criteria/{criterionId}")]
    public async Task<IActionResult> UpdateCriterion(string criterionId, [FromBody] UpdateCriterionRequest request)
    {
        // Hackathon simplification: criterionId codes (e.g. "INC-001") are looked up
        // by most-recent match. A single active protocol per demo keeps this unambiguous.
        var criterion = await _db.EligibilityCriteria
            .Where(c => c.CriterionId == criterionId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (criterion is null)
        {
            return NotFound(new { message = $"Criterion {criterionId} was not found." });
        }

        if (request.SimpleMeaning is not null) criterion.SimpleMeaning = request.SimpleMeaning;
        if (request.PatientQuestion is not null) criterion.PatientQuestion = request.PatientQuestion;
        if (request.Priority is not null) criterion.Priority = request.Priority;
        if (request.EligibilityImpact is not null) criterion.EligibilityImpact = request.EligibilityImpact;
        if (request.RequiresClinicalReview.HasValue) criterion.RequiresClinicalReview = request.RequiresClinicalReview.Value;

        criterion.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(ToCriterionResponse(criterion));
    }

    [HttpPost("protocols/{protocolId:guid}/criteria/approve")]
    public async Task<IActionResult> ApproveCriteria(Guid protocolId, [FromBody] ApproveCriteriaRequest? request)
    {
        var query = _db.EligibilityCriteria.Where(c => c.ProtocolId == protocolId);

        if (request?.CriterionIds is { Count: > 0 })
        {
            query = query.Where(c => request.CriterionIds.Contains(c.CriterionId));
        }

        var criteria = await query.ToListAsync();
        foreach (var criterion in criteria)
        {
            criterion.IsApproved = true;
            criterion.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        await _auditService.LogEventAsync(
            "CriteriaApproval",
            protocolId: protocolId,
            details: $"approvedCount={criteria.Count}");

        return Ok(new { protocolId, approvedCount = criteria.Count, criteria = criteria.Select(ToCriterionResponse) });
    }

    [HttpPost("protocols/{protocolId:guid}/generate-question-bank")]
    public async Task<IActionResult> GenerateQuestionBank(Guid protocolId)
    {
        var protocol = await _db.Protocols.FindAsync(protocolId);
        if (protocol is null)
        {
            return NotFound(new { message = $"Protocol {protocolId} was not found." });
        }

        var approvedCriteria = await _db.EligibilityCriteria
            .Where(c => c.ProtocolId == protocolId && c.IsApproved)
            .OrderBy(c => c.CriterionId)
            .ToListAsync();

        if (approvedCriteria.Count == 0)
        {
            return UnprocessableEntity(new { message = "No approved criteria found for this protocol. Approve criteria before generating a question bank." });
        }

        // Re-generation replaces any previously generated question bank for this protocol.
        var existingQuestions = _db.ScreeningQuestions.Where(q => q.ProtocolId == protocolId);
        _db.ScreeningQuestions.RemoveRange(existingQuestions);

        var result = await _questionBankService.GenerateAsync(protocolId, approvedCriteria);
        await _db.ScreeningQuestions.AddRangeAsync(result.Questions);

        protocol.Status = "QuestionsGenerated";
        await _db.SaveChangesAsync();

        await _auditService.LogEventAsync(
            "QuestionBankGeneration",
            protocolId: protocolId,
            details: $"usedFallback={result.UsedFallback}; questionCount={result.Questions.Count}");

        if (result.SuppressedDuplicateCriteria.Count > 0)
        {
            await _auditService.LogEventAsync(
                "QuestionDeduplication",
                protocolId: protocolId,
                details: $"suppressedCount={result.SuppressedDuplicateCriteria.Count}");
        }

        return Ok(new
        {
            protocolId,
            usedFallback = result.UsedFallback,
            fallbackReason = result.FallbackReason,
            sequencingRationale = result.SequencingRationale,
            suppressedDuplicateCriteria = result.SuppressedDuplicateCriteria,
            demographics = result.Questions.Where(q => q.IsDemographicQuestion).OrderBy(q => q.DisplayOrder).Select(ToQuestionResponse),
            criteria = result.Questions.Where(q => !q.IsDemographicQuestion).OrderBy(q => q.DisplayOrder).Select(ToQuestionResponse)
        });
    }

    [HttpGet("protocols/{protocolId:guid}/questions")]
    public async Task<IActionResult> GetQuestions(Guid protocolId)
    {
        var questions = await _db.ScreeningQuestions
            .Where(q => q.ProtocolId == protocolId)
            .OrderBy(q => q.IsDemographicQuestion ? 0 : 1)
            .ThenBy(q => q.DisplayOrder)
            .ToListAsync();

        return Ok(questions.Select(ToQuestionResponse));
    }

    [HttpGet("protocols/{protocolId:guid}/questions/sections")]
    public async Task<IActionResult> GetQuestionSections(Guid protocolId)
    {
        var questions = await _db.ScreeningQuestions
            .Where(q => q.ProtocolId == protocolId)
            .OrderBy(q => q.DisplayOrder)
            .ToListAsync();

        return Ok(new
        {
            demographics = questions.Where(q => q.IsDemographicQuestion).Select(ToQuestionResponse),
            criteria = questions.Where(q => !q.IsDemographicQuestion).Select(ToQuestionResponse)
        });
    }

    private static QuestionResponse ToQuestionResponse(ScreeningQuestion question) => new(
        question.QuestionId,
        question.Section,
        question.QuestionText,
        question.AnswerType,
        JsonHelpers.DeserializeStringArray(question.OptionsJson),
        question.Priority,
        question.DisplayOrder,
        JsonHelpers.DeserializeStringArray(question.LinkedCriteriaJson),
        JsonHelpers.DeserializeStringArray(question.CoveredCriteriaJson),
        question.SourceCriteriaText,
        question.WhyAsked,
        question.IsDemographicQuestion,
        question.IsDuplicateSuppressed,
        question.CanTriggerEarlyStop,
        question.EarlyStopReason,
        question.AppliesToSex,
        question.PromptVersion,
        question.ModelName);

    private static CriterionResponse ToCriterionResponse(EligibilityCriterion criterion)
    {
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(criterion.OptionsJson))
        {
            try
            {
                options = JsonSerializer.Deserialize<List<string>>(criterion.OptionsJson) ?? new List<string>();
            }
            catch (JsonException)
            {
                options = new List<string>();
            }
        }

        return new CriterionResponse(
            criterion.CriterionId,
            criterion.ProtocolId,
            criterion.Type,
            criterion.OriginalText,
            criterion.SimpleMeaning,
            criterion.PatientQuestion,
            criterion.AnswerType,
            options,
            criterion.EligibilityImpact,
            criterion.Priority,
            criterion.SourceSection,
            criterion.RequiresClinicalReview,
            criterion.CanBeCoveredByDemographics,
            criterion.AppliesToSex,
            criterion.IsApproved,
            criterion.PromptVersion,
            criterion.ModelName);
    }
}

public record UpdateCriterionRequest(
    string? SimpleMeaning,
    string? PatientQuestion,
    string? Priority,
    string? EligibilityImpact,
    bool? RequiresClinicalReview);

public record ApproveCriteriaRequest(List<string>? CriterionIds);

public record CriterionResponse(
    string CriterionId,
    Guid ProtocolId,
    string Type,
    string OriginalText,
    string? SimpleMeaning,
    string? PatientQuestion,
    string AnswerType,
    IReadOnlyList<string> Options,
    string EligibilityImpact,
    string Priority,
    string? SourceSection,
    bool RequiresClinicalReview,
    bool CanBeCoveredByDemographics,
    string? AppliesToSex,
    bool IsApproved,
    string? PromptVersion,
    string? ModelName);

public record QuestionResponse(
    string QuestionId,
    string Section,
    string QuestionText,
    string AnswerType,
    IReadOnlyList<string> Options,
    string Priority,
    int DisplayOrder,
    IReadOnlyList<string> LinkedCriteria,
    IReadOnlyList<string> CoveredCriteria,
    string? SourceCriteriaText,
    string? WhyAsked,
    bool IsDemographicQuestion,
    bool IsDuplicateSuppressed,
    bool CanTriggerEarlyStop,
    string? EarlyStopReason,
    string? AppliesToSex,
    string? PromptVersion,
    string? ModelName);
