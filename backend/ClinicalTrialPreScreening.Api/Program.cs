using System.Text.Json.Serialization;
using ClinicalTrialPreScreening.Api.Data;
using ClinicalTrialPreScreening.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.WriteIndented = true;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// The frontend (default http://localhost:5173) calls this API from the browser
// on a different origin/port, so CORS must be explicitly allowed.
const string FrontendCorsPolicy = "Frontend";
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        policy.WithOrigins(GetAllowedFrontendOrigins())
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(BuildConnectionString()));
builder.Services.AddHostedService<DatabaseInitializationService>();

builder.Services.AddSingleton<ProtocolTextExtractionService>();
builder.Services.AddSingleton<ProtocolChunkingService>();
builder.Services.AddSingleton<AiStatusService>();
builder.Services.AddHttpClient<ClaudeService>();
builder.Services.AddScoped<CriteriaExtractionService>();
builder.Services.AddScoped<QuestionBankService>();
builder.Services.AddSingleton<AdaptiveQuestionService>();
builder.Services.AddScoped<AnswerEvaluationService>();
builder.Services.AddScoped<ScreeningSessionService>();
builder.Services.AddHttpClient<EmbeddingClient>();
builder.Services.AddHttpClient<QdrantService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<ExportService>();

var app = builder.Build();

// Global safety net: any unhandled exception anywhere in the pipeline (e.g. SQL
// Server dropping mid-request) must degrade to a clear JSON error rather than
// crash the process or leak exception details. Never logs request bodies or
// connection strings — only the exception object and request path.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error is { } error)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");
            logger.LogError(error, "Unhandled exception processing {Path}.", context.Request.Path);
        }

        // WriteAsJsonAsync here bypasses MVC's configured (camelCase) JsonOptions,
        // so the naming policy is passed explicitly to match every other
        // controller-generated error body's lowercase "message" field.
        await context.Response.WriteAsJsonAsync(
            new { message = "An unexpected server error occurred. Please try again, and check server logs if the problem persists." },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(FrontendCorsPolicy);

app.MapControllers();

app.Run();

// Allows the frontend's origin(s) to call this API from the browser. Reads
// WEB_PORT so a remapped frontend port (see docker-compose.yml) still works.
static string[] GetAllowedFrontendOrigins()
{
    var webPort = Environment.GetEnvironmentVariable("WEB_PORT") ?? "5173";
    return new[] { $"http://localhost:{webPort}", $"http://127.0.0.1:{webPort}" };
}

// Resolves the SQL Server connection string: CONNECTION_STRING overrides
// everything; otherwise it is built from the individual SQLSERVER_* env vars.
// Never log the resolved value — it contains the SQL Server password.
static string BuildConnectionString()
{
    var overrideConnectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(overrideConnectionString))
    {
        return overrideConnectionString;
    }

    var host = Environment.GetEnvironmentVariable("SQLSERVER_HOST") ?? "sqlserver";
    var port = Environment.GetEnvironmentVariable("SQLSERVER_PORT") ?? "1433";
    var database = Environment.GetEnvironmentVariable("SQLSERVER_DATABASE") ?? "ClinicalTrialPreScreening";
    var user = Environment.GetEnvironmentVariable("SQLSERVER_USER") ?? "sa";
    var password = Environment.GetEnvironmentVariable("SQLSERVER_PASSWORD") ?? string.Empty;
    var trustCertificate = Environment.GetEnvironmentVariable("SQLSERVER_TRUST_CERTIFICATE") ?? "true";
    var encrypt = Environment.GetEnvironmentVariable("SQLSERVER_ENCRYPT") ?? "false";

    return $"Server={host},{port};Database={database};User Id={user};Password={password};" +
           $"TrustServerCertificate={trustCertificate};Encrypt={encrypt};";
}
