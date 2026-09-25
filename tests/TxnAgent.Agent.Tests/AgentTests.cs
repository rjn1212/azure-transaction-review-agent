using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TxnAgent.Data;
using static TxnAgent.Agent.Tests.ScriptedChatClient;

namespace TxnAgent.Agent.Tests;

public class AgentTests
{
    private static TxnDbContext NewSeededDb()
    {
        var db = new TxnDbContext(new DbContextOptionsBuilder<TxnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        DbInitializer.Seed(db, DateTime.UtcNow);
        db.SaveChanges();
        return db;
    }

    private static TransactionReviewAgent NewAgent(TxnDbContext db, IChatClient fake, int maxRunsPerDay = 200) =>
        new(db,
            new ChatClientBuilder(fake).UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 8).Build(),
            Options.Create(new AgentOptions { Model = "fake", InputPricePerMillion = 0.10m, OutputPricePerMillion = 0.50m, MaxRunsPerDay = maxRunsPerDay }));

    private static readonly string ValidJson =
        """{"decision":"HOLD","confidence":0.82,"reasons":["Amount is 12x typical spend"],"policyRulesCited":["R2"]}""";

    [Fact]
    public async Task Tools_return_small_payloads()
    {
        using var db = NewSeededDb();
        var alert = await db.Alerts.Include(a => a.Transaction).FirstAsync();
        var tools = new ReviewTools(db, alert.Transaction!.OccurredAt);

        var txn = await tools.GetTransactionAsync(alert.Id);
        Assert.NotNull(txn);
        var recent = await tools.GetRecentTransactionsAsync(txn!.CustomerId, 60, txn.TransactionId);
        Assert.InRange(recent.Count, 1, ReviewTools.MaxRecentTransactions);
        Assert.DoesNotContain(recent, r => r.Id == txn.TransactionId);
        Assert.Equal(6, (await tools.GetReviewPolicyAsync()).Count);
    }

    [Fact]
    public async Task Agent_calls_tools_then_saves_decision_and_trace()
    {
        using var db = NewSeededDb();
        var alertId = (await db.Alerts.FirstAsync()).Id;

        var fake = new ScriptedChatClient(
            _ => CallTool("c1", "get_transaction", new() { ["alertId"] = alertId }),
            _ => CallTool("c2", "get_review_policy", new()),
            _ => Text(ValidJson));

        var run = await NewAgent(db, fake).ReviewAsync(alertId);

        Assert.Equal("HOLD", run.Decision);
        Assert.Equal(0.82, run.Confidence, 3);
        Assert.Contains("R2", run.PolicyRulesJson);
        Assert.Equal(3, fake.Requests.Count); // 2 tool round-trips + final answer
        Assert.Equal(new[] { "get_transaction", "get_transaction", "get_review_policy", "get_review_policy" },
            run.Steps.Where(s => s.Kind is "ToolCall" or "ToolResult").Select(s => s.Name));
        Assert.True(run.CostUsd > 0);
        Assert.Equal(AlertStatus.Reviewed, (await db.Alerts.FindAsync(alertId))!.Status);
    }

    [Fact]
    public async Task Invalid_model_output_escalates()
    {
        using var db = NewSeededDb();
        var alertId = (await db.Alerts.FirstAsync()).Id;
        var fake = new ScriptedChatClient(_ => Text("""{"decision":"MAYBE","confidence":2,"reasons":[],"policyRulesCited":[]}"""));

        var run = await NewAgent(db, fake).ReviewAsync(alertId);

        Assert.Equal(Decisions.Escalate, run.Decision);
    }

    [Fact]
    public async Task Tool_windows_are_anchored_to_the_flagged_transaction_not_today()
    {
        using var db = new TxnDbContext(new DbContextOptionsBuilder<TxnDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seededAt = DateTime.UtcNow.AddDays(-365); // data seeded a year ago
        DbInitializer.Seed(db, seededAt);
        db.SaveChanges();
        var alert = await db.Alerts.Include(a => a.Transaction).FirstAsync();

        var tools = new ReviewTools(db, alert.Transaction!.OccurredAt);
        var recent = await tools.GetRecentTransactionsAsync(alert.Transaction.CustomerId, 30, alert.TransactionId);
        var profile = await tools.GetCustomerProfileAsync(alert.Transaction.CustomerId);

        Assert.NotEmpty(recent);
        Assert.True(profile!.TypicalTransactionAmount > 0);
    }

    [Fact]
    public async Task Daily_run_cap_blocks_further_llm_calls()
    {
        using var db = NewSeededDb();
        var alertId = (await db.Alerts.FirstAsync()).Id;
        var fake = new ScriptedChatClient(_ => Text(ValidJson));
        var agent = NewAgent(db, fake, maxRunsPerDay: 1);

        await agent.ReviewAsync(alertId);
        await Assert.ThrowsAsync<AgentLimitExceededException>(() => agent.ReviewAsync(alertId));
        Assert.Single(fake.Requests); // second review never reached the LLM
    }

    [Fact]
    public void Every_seeded_alert_has_an_expected_decision()
    {
        using var db = NewSeededDb();
        Assert.All(db.Alerts.ToList(), a => Assert.True(Decisions.IsValid(a.ExpectedDecision)));
    }
}
