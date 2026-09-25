namespace TxnAgent.Agent;

/// <summary>OpenAI key is missing. Mapped to HTTP 503.</summary>
public class AgentNotConfiguredException(string message) : Exception(message);

/// <summary>The daily agent-run cap was reached (cost guardrail). Mapped to HTTP 429.</summary>
public class AgentLimitExceededException(string message) : Exception(message);
