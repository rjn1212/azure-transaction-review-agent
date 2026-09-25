using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TxnAgent.Agent;
using TxnAgent.Data;

var builder = WebApplication.CreateBuilder(args);

// --- Data: Azure SQL in Azure; SQL Server in Docker locally (docker-compose.yml) ---
var sqlConn = builder.Configuration.GetConnectionString("Sql")
    ?? throw new InvalidOperationException("ConnectionStrings:Sql is not set.");
builder.Services.AddDbContext<TxnDbContext>(o =>
    o.UseSqlServer(sqlConn, sql => sql.EnableRetryOnFailure(5))); // retries help with serverless DB resume

// --- Agent: OpenAI via Microsoft.Extensions.AI ---
builder.Services.AddTransactionReviewAgent(builder.Configuration);

// --- CORS: the Angular app on Static Web Apps calls this API directly ---
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "http://localhost:4200")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

// --- Cost guardrail #1: throttle the endpoint that calls OpenAI (guardrail #2 is OpenAI:MaxRunsPerDay) ---
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("agent", w =>
    {
        w.PermitLimit = builder.Configuration.GetValue("RateLimit:AgentPerMinute", 10);
        w.Window = TimeSpan.FromMinutes(1);
        w.QueueLimit = 0;
    });
});

builder.Services.AddProblemDetails();
// Application Insights: enable auto-instrumentation on the App Service (Portal > Application Insights);
// no code change needed for requests, dependencies (SQL, OpenAI HTTP) and exceptions.

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();

// Create schema + seed synthetic data on startup (idempotent).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TxnDbContext>();
    await DbInitializer.InitializeAsync(db);
}

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

api.MapGet("/alerts", async (TxnDbContext db, string? status) =>
{
    var q = db.Alerts.AsNoTracking();
    if (Enum.TryParse<AlertStatus>(status, true, out var s)) q = q.Where(a => a.Status == s);
    var rows = await q
        .OrderByDescending(a => a.CreatedAt)
        .Select(a => new
        {
            a.Id, a.Reason, Status = a.Status.ToString(), a.CreatedAt,
            a.Transaction!.Amount, a.Transaction.Currency, a.Transaction.Country, a.Transaction.Channel,
            Merchant = a.Transaction.Merchant!.Name,
            Customer = a.Transaction.Customer!.Name,
            a.ExpectedDecision,
            LatestDecision = a.Runs.OrderByDescending(r => r.StartedAt).Select(r => r.FinalDecision ?? r.Decision).FirstOrDefault(),
        })
        .ToListAsync();
    return Results.Ok(rows);
});

api.MapGet("/alerts/{id:int}", async (int id, TxnDbContext db) =>
{
    var a = await db.Alerts.AsNoTracking()
        .Include(x => x.Transaction!).ThenInclude(t => t.Merchant)
        .Include(x => x.Transaction!).ThenInclude(t => t.Customer)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (a is null) return Results.NotFound();
    var runs = await db.AgentRuns.AsNoTracking().Where(r => r.AlertId == id)
        .OrderByDescending(r => r.StartedAt).Include(r => r.Steps).ToListAsync();
    return Results.Ok(new
    {
        a.Id, a.Reason, Status = a.Status.ToString(), a.CreatedAt, a.ExpectedDecision,
        Transaction = new
        {
            a.Transaction!.Id, a.Transaction.Amount, a.Transaction.Currency, a.Transaction.Channel,
            a.Transaction.Country, a.Transaction.OccurredAt,
            Merchant = a.Transaction.Merchant!.Name, Customer = a.Transaction.Customer!.Name,
        },
        Runs = runs.Select(ToDto),
    });
});

// The agentic call: the model chooses tools, we run them against SQL, it returns a decision.
api.MapPost("/alerts/{id:int}/review", async (int id, TransactionReviewAgent agent, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        var run = await agent.ReviewAsync(id, ct);
        return Results.Ok(ToDto(run));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (AgentNotConfiguredException ex) { return Results.Problem(ex.Message, statusCode: 503); }
    catch (AgentLimitExceededException ex) { return Results.Problem(ex.Message, statusCode: 429); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Typically OpenAI errors: bad key (401), quota/rate limit (429), model name wrong (404).
        log.LogError(ex, "Agent review failed for alert {AlertId}", id);
        return Results.Problem($"The LLM call failed: {ex.GetType().Name}. Check the API logs (App Insights) for details.", statusCode: 502);
    }
}).RequireRateLimiting("agent");

// Analyst accepts or overrides the agent's decision.
api.MapPost("/runs/{runId:int}/final", async (int runId, FinalDecisionRequest req, TxnDbContext db) =>
{
    if (!Decisions.IsValid(req.Decision)) return Results.BadRequest("Decision must be APPROVE, HOLD or ESCALATE.");
    var run = await db.AgentRuns.Include(r => r.Alert).FirstOrDefaultAsync(r => r.Id == runId);
    if (run is null) return Results.NotFound();
    run.FinalDecision = req.Decision.ToUpperInvariant();
    run.OverriddenBy = string.IsNullOrWhiteSpace(req.Analyst) ? "analyst" : req.Analyst;
    run.Alert!.Status = AlertStatus.Closed;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(run));
});

// Spend, analyst agreement and accuracy against the seeded expected decisions.
api.MapGet("/stats", async (TxnDbContext db) =>
{
    var runs = await db.AgentRuns.AsNoTracking()
        .Select(r => new { r.AlertId, r.StartedAt, r.CostUsd, r.InputTokens, r.OutputTokens, r.Decision, r.FinalDecision, r.Alert!.ExpectedDecision })
        .ToListAsync();
    var finals = runs.Where(r => r.FinalDecision != null).ToList();
    var latestLabelled = runs.Where(r => r.ExpectedDecision != null)
        .GroupBy(r => r.AlertId).Select(g => g.OrderByDescending(r => r.StartedAt).First()).ToList();
    return Results.Ok(new
    {
        Runs = runs.Count,
        TotalCostUsd = runs.Sum(r => r.CostUsd),
        InputTokens = runs.Sum(r => r.InputTokens),
        OutputTokens = runs.Sum(r => r.OutputTokens),
        AnalystAgreementRate = finals.Count == 0 ? (double?)null : finals.Count(r => r.FinalDecision == r.Decision) / (double)finals.Count,
        LabelledAlertsReviewed = latestLabelled.Count,
        AccuracyVsExpected = latestLabelled.Count == 0 ? (double?)null : latestLabelled.Count(r => r.Decision == r.ExpectedDecision) / (double)latestLabelled.Count,
    });
});

// Demo helper: clear agent runs and reopen all alerts (does not call OpenAI). Disable with Demo:AllowReset=false.
api.MapPost("/demo/reset", async (TxnDbContext db, IConfiguration cfg) =>
{
    if (!cfg.GetValue("Demo:AllowReset", true)) return Results.NotFound();
    await db.AgentSteps.ExecuteDeleteAsync();
    await db.AgentRuns.ExecuteDeleteAsync();
    await db.Alerts.ExecuteUpdateAsync(a => a.SetProperty(x => x.Status, AlertStatus.Open));
    return Results.NoContent();
});

// Demo helper: wipe ALL data and re-seed from scratch. Use after changing seed data in DbInitializer.
api.MapPost("/demo/reseed", async (TxnDbContext db, IConfiguration cfg) =>
{
    if (!cfg.GetValue("Demo:AllowReset", true)) return Results.NotFound();
    await db.AgentSteps.ExecuteDeleteAsync();
    await db.AgentRuns.ExecuteDeleteAsync();
    await db.Alerts.ExecuteDeleteAsync();
    await db.Transactions.ExecuteDeleteAsync();
    await db.Merchants.ExecuteDeleteAsync();
    await db.Customers.ExecuteDeleteAsync();
    await db.PolicyRules.ExecuteDeleteAsync();
    DbInitializer.Seed(db, DateTime.UtcNow);
    await db.SaveChangesAsync();
    var count = await db.Alerts.CountAsync();
    return Results.Ok(new { reseeded = true, alerts = count });
});

app.Run();

static object ToDto(AgentRun r) => new
{
    r.Id, r.AlertId, r.Model, r.Decision, r.Confidence,
    Reasons = JsonSerializer.Deserialize<List<string>>(r.ReasonsJson) ?? [],
    PolicyRules = JsonSerializer.Deserialize<List<string>>(r.PolicyRulesJson) ?? [],
    r.InputTokens, r.OutputTokens, r.CostUsd, r.StartedAt, r.DurationMs, r.FinalDecision, r.OverriddenBy,
    Steps = r.Steps.OrderBy(s => s.StepNo).Select(s => new { s.StepNo, s.Kind, s.Name, s.PayloadJson }),
};

record FinalDecisionRequest(string Decision, string? Analyst);

public partial class Program;
