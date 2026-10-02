using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using SdlcStudio.Api.Ai.Mock;
using SdlcStudio.Api.Options;

namespace SdlcStudio.Api.Ai;

/// <summary>
/// Creates the <see cref="IChatClient"/> each agent talks to.
/// </summary>
/// <remarks>
/// <para>
/// WHY a factory keyed by agent name instead of registering one IChatClient in DI?
/// In Mock mode each agent gets its own scripted client that knows how *that* agent should answer.
/// With a real model every agent shares one underlying client. A factory also lets you route agents to
/// different models later: a cheap, fast model for the interviewer and a stronger one for the developer.
/// </para>
/// <para>
/// WHY Microsoft.Extensions.AI? <see cref="IChatClient"/> is the provider-neutral abstraction the Agent
/// Framework builds on. Swapping OpenAI for Azure OpenAI, GitHub Models, Ollama or Anthropic is a change in
/// this one file; agents, workflows and tests do not care.
/// </para>
/// </remarks>
public sealed class ChatClientFactory(IOptions<LlmOptions> options, ILoggerFactory loggerFactory)
{
    private readonly LlmOptions _options = options.Value;

    // The real client is created once and shared: the OpenAI SDK client is thread safe and pools
    // HTTP connections. `field ??=` (C# 14 field keyword) gives us lazy init without a backing field.
    private IChatClient SharedClient => field ??= CreateRealClient();

    public LlmProvider Provider => _options.Provider;
    public string Model => _options.Provider == LlmProvider.Mock ? "scripted-mock" : _options.Model;

    public IChatClient Create(string agentKey) => _options.Provider switch
    {
        LlmProvider.Mock => new ScriptedChatClient(agentKey),
        _ => SharedClient,
    };

    private IChatClient CreateRealClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException(
                $"Llm:Provider is {_options.Provider} but Llm:ApiKey is empty. Run: dotnet user-secrets set \"Llm:ApiKey\" \"<key>\"");

        // Azure OpenAI and GitHub Models both expose OpenAI-compatible endpoints, so one SDK covers all three.
        // ALTERNATIVES:
        //  - Azure.AI.OpenAI's AzureOpenAIClient with DefaultAzureCredential (keyless, Entra ID auth):
        //      new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential()).GetChatClient(model).AsIChatClient()
        //  - Azure AI Foundry Agents (persistent, server-side agents) via Microsoft.Agents.AI.AzureAI.
        //  - Local models with OllamaSharp or Foundry Local, which also implement IChatClient.
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
            clientOptions.Endpoint = new Uri(_options.Endpoint);

        IChatClient inner = new OpenAIClient(new ApiKeyCredential(_options.ApiKey), clientOptions)
            .GetChatClient(_options.Model)
            .AsIChatClient();

        // IChatClient middleware pipeline (same idea as ASP.NET Core middleware).
        // NOTE: we do NOT add UseFunctionInvocation() here; ChatClientAgent adds its own function
        // invocation layer around whatever client it is given.
        // ALTERNATIVES worth adding in production: .UseOpenTelemetry() for traces of every model call,
        // .UseDistributedCache() to cache identical prompts, or a rate limiting / retry layer.
        return inner.AsBuilder()
            .UseLogging(loggerFactory)
            .Build();
    }
}
