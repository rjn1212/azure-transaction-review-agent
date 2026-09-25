using System.ComponentModel;

namespace TxnAgent.Agent;

/// <summary>The structured result the model must return (enforced via JSON schema).</summary>
public record AgentDecision(
    [property: Description("One of APPROVE, HOLD, ESCALATE")] string Decision,
    [property: Description("0.0 to 1.0")] double Confidence,
    [property: Description("Short, specific reasons referencing the data you looked up")] List<string> Reasons,
    [property: Description("Codes of the policy rules applied, e.g. R2")] List<string> PolicyRulesCited);

public static class Decisions
{
    public const string Approve = "APPROVE";
    public const string Hold = "HOLD";
    public const string Escalate = "ESCALATE";
    public static readonly string[] All = [Approve, Hold, Escalate];

    public static bool IsValid(string? d) => d is not null && All.Contains(d.ToUpperInvariant());
}
