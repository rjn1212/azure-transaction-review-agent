namespace TxnAgent.Data;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string HomeCountry { get; set; } = "US";
    public DateOnly OpenedOn { get; set; }
    public decimal AvgMonthlySpend { get; set; }
    public string CardStatus { get; set; } = "Active";
}

public class Merchant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Mcc { get; set; } = "";
    public string Category { get; set; } = "";
    public string Country { get; set; } = "US";
    public int RiskScore { get; set; }            // 0 (safe) .. 100 (risky)
    public decimal ChargebackRate { get; set; }   // e.g. 0.004 = 0.4%
}

public class Transaction
{
    public long Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int MerchantId { get; set; }
    public Merchant? Merchant { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Channel { get; set; } = "CardPresent"; // CardPresent | Online | ATM
    public string Country { get; set; } = "US";
    public DateTime OccurredAt { get; set; }
}

public enum AlertStatus { Open, Reviewed, Closed }

public class Alert
{
    public int Id { get; set; }
    public long TransactionId { get; set; }
    public Transaction? Transaction { get; set; }
    public string Reason { get; set; } = "";
    public AlertStatus Status { get; set; } = AlertStatus.Open;
    public DateTime CreatedAt { get; set; }
    /// <summary>Ground truth for evaluating the agent on seeded scenarios (null = no label).</summary>
    public string? ExpectedDecision { get; set; }
    public List<AgentRun> Runs { get; set; } = [];
}

public class PolicyRule
{
    public string Code { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class AgentRun
{
    public int Id { get; set; }
    public int AlertId { get; set; }
    public Alert? Alert { get; set; }
    public string Model { get; set; } = "";
    public string Decision { get; set; } = "";   // APPROVE | HOLD | ESCALATE
    public double Confidence { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public string PolicyRulesJson { get; set; } = "[]";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTime StartedAt { get; set; }
    public int DurationMs { get; set; }
    public string? FinalDecision { get; set; }   // analyst's decision (may differ)
    public string? OverriddenBy { get; set; }
    public List<AgentStep> Steps { get; set; } = [];
}

public class AgentStep
{
    public int Id { get; set; }
    public int AgentRunId { get; set; }
    public int StepNo { get; set; }
    public string Kind { get; set; } = "";       // ToolCall | ToolResult | ModelText
    public string Name { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}
