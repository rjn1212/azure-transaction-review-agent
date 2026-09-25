using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace TxnAgent.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the IChatClient pipeline: OpenAI -> function invocation (runs our tools) -> logging.
    /// If no API key is configured the app still starts; agent calls return a clear error.
    /// </summary>
    public static IServiceCollection AddTransactionReviewAgent(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AgentOptions>(config.GetSection("OpenAI"));
        var opts = config.GetSection("OpenAI").Get<AgentOptions>() ?? new AgentOptions();

        services.AddSingleton<IChatClient>(sp =>
        {
#pragma warning disable OPENAI001 // Responses API is experimental
            IChatClient inner = string.IsNullOrWhiteSpace(opts.ApiKey)
                ? new NotConfiguredChatClient()
                : new OpenAIClient(opts.ApiKey).GetResponsesClient().AsIChatClient(opts.Model);
#pragma warning restore OPENAI001

            return new ChatClientBuilder(inner)
                .UseFunctionInvocation(sp.GetService<ILoggerFactory>(), c => c.MaximumIterationsPerRequest = opts.MaxIterations)
                .UseLogging(sp.GetService<ILoggerFactory>())
                .Build(sp);
        });

        services.AddScoped<TransactionReviewAgent>();
        return services;
    }
}

internal sealed class NotConfiguredChatClient : IChatClient
{
    private const string Message = "OpenAI is not configured. Set OpenAI:ApiKey (user-secrets locally, OpenAI__ApiKey app setting in Azure).";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new AgentNotConfiguredException(Message);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new AgentNotConfiguredException(Message);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
