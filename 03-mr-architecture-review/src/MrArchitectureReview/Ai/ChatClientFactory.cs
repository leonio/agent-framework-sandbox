using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;
using OpenAI;

namespace MrArchitectureReview.Ai;

/// <summary>
/// Creates the <see cref="IChatClient"/> each agent talks to.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY everything goes through <c>IChatClient</c>.</b> <c>Microsoft.Extensions.AI.IChatClient</c> is the
/// provider-neutral abstraction the Agent Framework's <c>ChatClientAgent</c> is built on. The agents,
/// the workflow and the tests never know whether they are talking to OpenAI, Azure, GitHub Models,
/// Ollama, or the offline <see cref="ScriptedChatClient"/>. Swapping providers is a config change.
/// </para>
/// <para>
/// <b>WHY a per-agent factory method</b> (<see cref="CreateFor"/>) rather than one shared client?
/// Real providers return the same underlying client for everyone (it is thread-safe and pools HTTP
/// connections). The mock needs to know <i>which</i> agent is calling so it can return that agent's
/// script. It also leaves the door open to per-agent models: e.g. a cheap model for the extensibility
/// reviewer and a stronger one for security. That is a common, effective cost optimisation.
/// </para>
/// <para>
/// <b>Function calling.</b> We do not add <c>.UseFunctionInvocation()</c> here: <c>ChatClientAgent</c>
/// wraps the client in a <c>FunctionInvokingChatClient</c> automatically, so tools passed to the agent
/// are executed for us. Adding it twice would double-wrap.
/// </para>
/// </remarks>
public sealed class ChatClientFactory(IOptions<ReviewOptions> options, ILoggerFactory loggerFactory)
{
    private readonly AiOptions _ai = options.Value.Ai;

    // Lazy: the real client is only built if a non-mock provider is configured, so missing keys
    // never break the offline experience.
    private IChatClient? _shared;

    public string ProviderName => _ai.Provider.ToString();
    public string ModelName => _ai.Provider is AiProvider.Mock ? "scripted" : _ai.Model;

    public IChatClient CreateFor(string agentName) => _ai.Provider switch
    {
        AiProvider.Mock => new ScriptedChatClient(
            agentName,
            Path.Combine(AppContext.BaseDirectory, "MockResponses"),
            loggerFactory.CreateLogger<ScriptedChatClient>()),

        // `??=` caches the shared client on first use.
        _ => _shared ??= BuildRealClient(),
    };

    private IChatClient BuildRealClient()
    {
        var (endpoint, key) = _ai.Provider switch
        {
            AiProvider.OpenAI => (_ai.Endpoint, _ai.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")),

            // Azure OpenAI now exposes an OpenAI-compatible "v1" surface, so the plain OpenAI SDK works
            // against it and we avoid a second SDK (Azure.AI.OpenAI) with its own API versions.
            // Endpoint format: https://<resource>.openai.azure.com/openai/v1/
            // Keyless (recommended in production): add Azure.Identity and use
            //   new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(),
            //       "https://cognitiveservices.azure.com/.default"), clientOptions)
            // so no key ever lives in config.
            AiProvider.AzureOpenAI => (_ai.Endpoint ?? throw Missing("Review:Ai:Endpoint"), _ai.ApiKey),

            // GitHub Models: free tier for prototyping with a GitHub PAT (models:read scope).
            // Model ids are namespaced, e.g. "openai/gpt-4.1-mini".
            AiProvider.GitHubModels => (_ai.Endpoint ?? "https://models.github.ai/inference",
                                        _ai.ApiKey ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")),

            _ => throw new NotSupportedException($"Unknown provider {_ai.Provider}"),
        };

        if (string.IsNullOrWhiteSpace(key))
            throw Missing("Review:Ai:ApiKey (or OPENAI_API_KEY / GITHUB_TOKEN)");

        var clientOptions = new OpenAIClientOptions();
        if (endpoint is not null)
            clientOptions.Endpoint = new Uri(endpoint);

        return new OpenAIClient(new ApiKeyCredential(key), clientOptions)
            .GetChatClient(_ai.Model)
            .AsIChatClient()
            // The IChatClient pipeline is middleware, like ASP.NET Core. Logging here records every
            // request/response at Trace level. Other useful layers (not enabled to keep output clean):
            //   .UseOpenTelemetry()            - spans + token metrics, export to Aspire/App Insights
            //   .UseDistributedCache(cache)    - replay identical prompts for free while iterating
            // Docs: https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai
            .AsBuilder()
            .UseLogging(loggerFactory)
            .Build();
    }

    private static InvalidOperationException Missing(string setting) =>
        new($"{setting} is not configured. Set it with `dotnet user-secrets set \"{setting.Split(' ')[0]}\" <value>` " +
            "or switch back to the offline provider with --Review:Ai:Provider=Mock.");
}
