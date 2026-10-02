using System.ClientModel;
using CliDesignPipeline.Ai.Mock;
using CliDesignPipeline.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace CliDesignPipeline.Ai;

/// <summary>
/// Creates the <see cref="IChatClient"/> each agent runs on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why IChatClient?</b> <see cref="IChatClient"/> (Microsoft.Extensions.AI) is the
/// provider-neutral abstraction that Agent Framework's <c>ChatClientAgent</c> is built on. Every
/// agent in this sample is "a chat client + instructions + tools", so swapping OpenAI for Azure
/// OpenAI, GitHub Models, Ollama, Foundry Local or the offline mock is a change in this one
/// file. Nothing downstream knows or cares.
/// </para>
/// <para>
/// <b>One client per agent</b> (rather than one shared client) so each agent can use a
/// different model (<c>Pipeline:Models:developer = gpt-4.1</c>) and so the mock can script
/// each agent separately. Clients are cheap; the underlying HTTP pipeline is shared by the SDK.
/// </para>
/// <para>
/// <b>Middleware.</b> <c>AsBuilder()...Build()</c> composes decorators around the raw client.
/// We add logging. Other useful ones, in the order you would usually stack them:
/// <c>.UseOpenTelemetry()</c> (traces with token counts), <c>.UseDistributedCache(cache)</c>
/// (replay identical prompts for free in dev), <c>.Use(rateLimiter)</c>. We do <i>not</i> add
/// <c>.UseFunctionInvocation()</c>: <c>ChatClientAgent</c> adds it for us when the agent has tools.
/// </para>
/// </remarks>
public sealed class ChatClientFactory(IOptions<PipelineOptions> options, ILoggerFactory loggerFactory)
{
    public IChatClient Create(string agentName)
    {
        var o = options.Value;

        var inner = o.Provider switch
        {
            ModelProvider.Mock => new ScriptedChatClient(agentName, MockScripts.For(agentName)),
            _ => CreateOpenAICompatible(o, o.ModelFor(agentName)),
        };

        return inner.AsBuilder()
            .UseLogging(loggerFactory)
            .Build();
    }

    /// <summary>
    /// OpenAI, Azure OpenAI (v1 API) and GitHub Models all speak the OpenAI wire protocol, so one
    /// SDK and a different endpoint covers all three.
    /// </summary>
    /// <remarks>
    /// Keyless auth for Azure (recommended in production) uses Entra ID instead of an API key:
    /// <code>
    /// // dotnet add package Azure.Identity
    /// var client = new OpenAIClient(
    ///     new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"),
    ///     new OpenAIClientOptions { Endpoint = endpoint });
    /// </code>
    /// For server-side agents (threads stored in the service, hosted tools such as code
    /// interpreter / file search) use the Foundry agents provider instead of a chat client.
    /// </remarks>
    private static IChatClient CreateOpenAICompatible(PipelineOptions o, string model)
    {
        var apiKey = o.ApiKey
            ?? throw new InvalidOperationException(
                $"Pipeline:ApiKey is required for provider {o.Provider}. Set it with: dotnet user-secrets set Pipeline:ApiKey <key>");

        var endpoint = o.Endpoint ?? o.Provider switch
        {
            ModelProvider.OpenAI => null, // SDK default: https://api.openai.com/v1
            ModelProvider.GitHubModels => new Uri("https://models.github.ai/inference"),
            ModelProvider.AzureOpenAI => throw new InvalidOperationException(
                "Pipeline:Endpoint is required for AzureOpenAI, e.g. https://<resource>.openai.azure.com/openai/v1/"),
            _ => throw new ArgumentOutOfRangeException(nameof(o), o.Provider, null),
        };

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });

        // Chat Completions API. Alternative: client.GetResponsesClient().AsIChatClient(model) for the
        // newer Responses API (server-side conversation state, reasoning summaries, hosted tools).
        return client.GetChatClient(model).AsIChatClient();
    }
}
