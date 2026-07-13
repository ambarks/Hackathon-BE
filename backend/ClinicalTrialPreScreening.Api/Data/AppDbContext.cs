using ClinicalTrialPreScreening.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClinicalTrialPreScreening.Api.Data;

// Entities intentionally have no navigation properties (only scalar Guid
// foreign-key-style columns, e.g. ProtocolId). This keeps EnsureCreatedAsync's
// generated schema simple and, per CLAUDE.md, avoids EF Core JSON reference
// cycles by construction rather than relying solely on ReferenceHandler.IgnoreCycles.
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Protocol> Protocols => Set<Protocol>();
    public DbSet<ProtocolChunk> ProtocolChunks => Set<ProtocolChunk>();
    public DbSet<EligibilityCriterion> EligibilityCriteria => Set<EligibilityCriterion>();
    public DbSet<ScreeningQuestion> ScreeningQuestions => Set<ScreeningQuestion>();
    public DbSet<ScreeningSession> ScreeningSessions => Set<ScreeningSession>();
    public DbSet<ScreeningAnswer> ScreeningAnswers => Set<ScreeningAnswer>();
    public DbSet<ScreeningSummary> ScreeningSummaries => Set<ScreeningSummary>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
}
