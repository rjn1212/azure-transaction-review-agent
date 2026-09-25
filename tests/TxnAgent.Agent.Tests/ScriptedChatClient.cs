using Microsoft.Extensions.AI;

namespace TxnAgent.Agent.Tests;

/// <summary>
/// A fake LLM: returns pre-scripted responses in order. Lets us test the full agent loop
/// (tool calls executed by FunctionInvokingChatClient, then a JSON decision) at zero OpenAI cost.
/// </summary>
public sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] script) : IChatClient
{
    private int _turn;
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        Requests.Add(history);
        var reply = script[Math.Min(_turn++, script.Length - 1)](history);
        return Task.FromResult(new ChatResponse(reply)
        {
            Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 100 },
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    public static ChatMessage CallTool(string callId, string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]);

    public static ChatMessage Text(string text) => new(ChatRole.Assistant, text);
}
