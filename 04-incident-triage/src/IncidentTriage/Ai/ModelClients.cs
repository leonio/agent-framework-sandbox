using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using IncidentTriage.Ai.Mock;
using IncidentTriage.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;

namespace IncidentTriage.Ai;

/// <summary>
/// Creates the <see cref="IChatClient"/>s and the <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// every other class depends on.
/// </summary>
/// <remarks>
/// <para><b>WHY Microsoft.Extensions.AI (MEAI) as the seam?</b> Agent Framework's <c>ChatClientAgent</c>
/// accepts any <c>IChatClient</c>, and the vector store accepts any <c>IEmbeddingGenerator</c>. Picking the
/// provider is therefore one switch statement in one class, and the offline mocks are first-class
/// citizens rather than test-only hacks. Docs: https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai</para>
///
/// <para><b>WHY the OpenAI SDK for Azure too?</b> Azure OpenAI / Azure AI Foundry expose an
/// OpenAI-compatible <c>/openai/v1/</c> endpoint, so the plain OpenAI client works against both. This
/// avoids the separate Azure.AI.OpenAI package and its api-version juggling.
/// Docs: https://learn.microsoft.com/azure/ai-foundry/openai/api-version-lifecycle</para>
///
/// <para><b>Alternatives:</b> <c>Azure.AI.Projects</c> + <c>PersistentAgentsClient</c> gives you
/// server-side (hosted) agents with threads stored in Foundry; <c>OllamaSharp</c> or
/// <c>Microsoft.Extensions.AI.Ollama</c> for local models; any provider with an MEAI adapter
/// (Anthropic, Gemini, Bedrock via community packages) drops in the same way.</para>
/// </remarks>
public sealed class ModelClients(AiOptions options)
{
    // Lazily created and shared: OpenAIClient owns an HTTP pipeline and is meant to be reused.
    private readonly Lazy<OpenAIClient> _openAi = new(() => CreateOpenAIClient(options));

    public bool IsOffline => options.Provider == AiProvider.Mock;

    public string Describe() => options.Provider switch
    {
        AiProvider.Mock => "offline scripted mock (set Ai:Provider to AzureOpenAI or OpenAI for a real model)",
        _ => $"{options.Provider} chat='{options.ChatModel}' embeddings='{options.EmbeddingModel}'",
    };

    /// <summary>
    /// One chat client per agent. With a real provider every agent shares the same model, but the
    /// per-agent call lets the offline mock script each agent separately, and is where you would route
    /// cheap agents (signal extraction) to a small model and the root-cause agent to a reasoning model.
    /// </summary>
    public IChatClient CreateChatClient(string agentName) => options.Provider switch
    {
        AiProvider.Mock => new ScriptedChatClient(agentName),
        _ => _openAi.Value.GetChatClient(options.ChatModel).AsIChatClient()
            .AsBuilder()
            // Emits gen_ai.* spans/metrics (OpenTelemetry semantic conventions). Wire an exporter
            // (Aspire dashboard, Azure Monitor, Jaeger) to see every prompt, token count and tool call.
            .UseOpenTelemetry(sourceName: "IncidentTriage", configure: c => c.EnableSensitiveData = false)
            // NOTE: no .UseFunctionInvocation() here. ChatClientAgent adds its own function-invoking
            // layer; adding a second one would run every tool call loop twice.
            .Build(),
    };

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator() => options.Provider switch
    {
        AiProvider.Mock => new HashingEmbeddingGenerator(),
        _ => _openAi.Value.GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator(),
    };

    private static OpenAIClient CreateOpenAIClient(AiOptions o)
    {
        switch (o.Provider)
        {
            case AiProvider.AzureOpenAI:
            {
                var endpoint = o.Endpoint ?? throw new InvalidOperationException("Ai:Endpoint is required for AzureOpenAI.");
                var clientOptions = new OpenAIClientOptions { Endpoint = new Uri($"{endpoint.TrimEnd('/')}/openai/v1/") };

                // Prefer keyless Entra ID auth. DefaultAzureCredential is convenient for samples (it tries
                // az login, VS, env vars, managed identity...). In production pin the exact credential,
                // e.g. ManagedIdentityCredential, to avoid slow probing and surprising fall-throughs.
                // OPENAI001: the AuthenticationPolicy constructor is marked "evaluation" in OpenAI 2.x. It is the
                // documented way to use Entra ID tokens with the v1 endpoint, so we accept that and suppress.
#pragma warning disable OPENAI001
                return string.IsNullOrWhiteSpace(o.ApiKey)
                    ? new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"), clientOptions)
                    : new OpenAIClient(new ApiKeyCredential(o.ApiKey), clientOptions);
#pragma warning restore OPENAI001
            }
            case AiProvider.OpenAI:
            {
                var key = o.ApiKey ?? throw new InvalidOperationException("Ai:ApiKey is required for OpenAI.");
                var clientOptions = new OpenAIClientOptions();
                clientOptions.Endpoint = string.IsNullOrWhiteSpace(o.Endpoint) ? clientOptions.Endpoint : new Uri(o.Endpoint);
                return new OpenAIClient(new ApiKeyCredential(key), clientOptions);
            }
            default:
                throw new InvalidOperationException($"Provider {o.Provider} does not use OpenAIClient.");
        }
    }
}
