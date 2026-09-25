using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TxnAgent.Data;

namespace TxnAgent.Agent;

/// <summary>
/// Runs one agentic review: the model decides which tools to call (the FunctionInvokingChatClient
/// in the IChatClient pipeline executes them), then returns a structured AgentDecision.
/// The decision and a full step trace are saved to SQL.
/// </summary>
public class TransactionReviewAgent(TxnDbContext db, IChatClient chat, IOptions<AgentOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AgentOptions _opts = options.Value;

    public async Task<AgentRun> ReviewAsync(int alertId, CancellationToken ct = default)
    {
        var alert = await db.Alerts.Include(a => a.Transaction).FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new KeyNotFoundException($"Alert {alertId} not found");

        // Cost guardrail: refuse once today's run count reaches the cap.
        var todayUtc = DateTime.UtcNow.Date;
        var runsToday = await db.AgentRuns.CountAsync(r => r.StartedAt >= todayUtc, ct);
        if (runsToday >= _opts.MaxRunsPerDay)
            throw new AgentLimitExceededException($"Daily agent run limit ({_opts.MaxRunsPerDay}) reached. Try again tomorrow or raise OpenAI:MaxRunsPerDay.");

        var tools = new ReviewTools(db, alert.Transaction!.OccurredAt);
        var chatOptions = new ChatOptions
        {
            Tools = tools.AsAITools(),
            ToolMode = ChatToolMode.Auto,
            MaxOutputTokens = _opts.MaxOutputTokens,
        };
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Prompts.System),
            new(ChatRole.User, Prompts.UserTask(alertId, alert.Reason)),
        ];

        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var response = await chat.GetResponseAsync<AgentDecision>(messages, chatOptions, cancellationToken: ct);
        sw.Stop();

        var run = new AgentRun
        {
            AlertId = alertId,
            Model = _opts.Model,
            StartedAt = started,
            DurationMs = (int)sw.ElapsedMilliseconds,
            InputTokens = response.Usage?.InputTokenCount ?? 0,
            OutputTokens = response.Usage?.OutputTokenCount ?? 0,
            Steps = BuildTrace(response.Messages),
        };
        run.CostUsd = Math.Round(
            run.InputTokens * _opts.InputPricePerMillion / 1_000_000m +
            run.OutputTokens * _opts.OutputPricePerMillion / 1_000_000m, 6);

        // Guardrail: anything unparseable or invalid becomes ESCALATE (a human looks at it).
        if (response.TryGetResult(out var decision) && decision is not null && Decisions.IsValid(decision.Decision))
        {
            run.Decision = decision.Decision.ToUpperInvariant();
            run.Confidence = Math.Clamp(decision.Confidence, 0, 1);
            run.ReasonsJson = JsonSerializer.Serialize(decision.Reasons ?? [], Json);
            run.PolicyRulesJson = JsonSerializer.Serialize(decision.PolicyRulesCited ?? [], Json);
        }
        else
        {
            run.Decision = Decisions.Escalate;
            run.Confidence = 0;
            run.ReasonsJson = JsonSerializer.Serialize(new[] { "Agent did not return a valid decision; escalated for human review." }, Json);
        }

        alert.Status = AlertStatus.Reviewed;
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private static string Truncate(string s, int max = 8000) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Turns the conversation (tool calls, tool results, model text) into ordered trace steps.</summary>
    public static List<AgentStep> BuildTrace(IEnumerable<ChatMessage> messages)
    {
        var steps = new List<AgentStep>();
        var callNames = new Dictionary<string, string>();
        int n = 0;
        foreach (var msg in messages)
        {
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        callNames[call.CallId] = call.Name;
                        steps.Add(new AgentStep { StepNo = ++n, Kind = "ToolCall", Name = call.Name, PayloadJson = JsonSerializer.Serialize(call.Arguments, Json) });
                        break;
                    case FunctionResultContent result:
                        steps.Add(new AgentStep
                        {
                            StepNo = ++n, Kind = "ToolResult",
                            Name = callNames.GetValueOrDefault(result.CallId, "unknown"),
                            PayloadJson = Truncate(JsonSerializer.Serialize(result.Result, Json)),
                        });
                        break;
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        steps.Add(new AgentStep { StepNo = ++n, Kind = "ModelText", Name = msg.Role.Value, PayloadJson = JsonSerializer.Serialize(text.Text, Json) });
                        break;
                }
            }
        }
        return steps;
    }
}
