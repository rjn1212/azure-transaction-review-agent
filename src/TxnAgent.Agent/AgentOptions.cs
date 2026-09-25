namespace TxnAgent.Agent;

/// <summary>Bound from the "OpenAI" config section (App Service setting names use OpenAI__X).</summary>
public class AgentOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-6-luna";

    /// <summary>Max model round-trips (tool-call iterations) per review. Caps cost.</summary>
    public int MaxIterations { get; set; } = 8;

    public int MaxOutputTokens { get; set; } = 1500;

    /// <summary>Hard cap on agent runs per UTC day, so a leaked URL or a stuck client can't run up a bill.</summary>
    public int MaxRunsPerDay { get; set; } = 200;

    /// <summary>USD per 1M tokens, used to record cost per run. Update if you change models.</summary>
    public decimal InputPricePerMillion { get; set; } = 0.10m;
    public decimal OutputPricePerMillion { get; set; } = 0.50m;
}
