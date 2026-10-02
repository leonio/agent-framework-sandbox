using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using Roster.Agents.Runtime.Fake;

namespace Roster.Agents.Runtime;

public sealed class ModelClientOptions
{
    /// <summary>The OpenTelemetry source and meter name for model calls. Hosts add it to their tracing and metrics.</summary>
    public const string TelemetrySourceName = "Roster.Models";

    /// <summary>
    /// Whether model-call spans include the prompts and replies. Null (the default) follows the standard
    /// <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> variable, which is off unless set. Aspire sets it for
    /// local runs so the dashboard can show conversations. Prompts carry untrusted text and personal data, so leave it
    /// off anywhere shared.
    /// </summary>
    public bool? CaptureMessageContent { get; set; }
}

/// <summary>
/// Builds the <see cref="IChatClient"/> for one agent call from a <see cref="ResolvedModel"/> (the endpoint after
/// routing, with its key decrypted in memory).
/// </summary>
/// <remarks>
/// <para>The pipeline, outermost first:</para>
/// <list type="number">
/// <item><b>OpenTelemetry</b>: a <c>chat</c> span per model call with the GenAI semantic conventions (model, tokens,
/// finish reason), under <see cref="ModelClientOptions.TelemetrySourceName"/>.</item>
/// <item><b>Concurrency gate</b>: waits for a slot on the endpoint's semaphore (<see cref="EndpointGates"/>).</item>
/// <item><b>The provider client</b>: <c>openai</c> for anything OpenAI-compatible (OpenAI, Azure OpenAI / Foundry,
/// Ollama, vLLM, LiteLLM), or <c>fake</c>.</item>
/// </list>
/// <para>Agent Framework's <c>ChatClientAgent</c> then adds its function-invoking layer on top, which runs tool calls.</para>
/// <para>A client is built per call and not cached: it would otherwise hold a person's decrypted key for longer than the
/// call needs it. Building one is cheap because the OpenAI SDK shares its HTTP transport.</para>
/// </remarks>
public sealed class ChatClientFactory(EndpointGates gates, IOptions<ModelClientOptions> options, ILoggerFactory loggerFactory)
{
    public IChatClient Create(ResolvedModel model, AgentDefinition agent)
    {
        IChatClient provider = model.Kind.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAI(model),

            // The fake needs the agent's name to know which heuristics to apply (see FakeChatClient).
            "fake" => new FakeChatClient(agent.Name, model.Model),

            "copilot" => throw new NotSupportedException(
                "Copilot endpoints arrive in slice 2, with the sandbox runner pool that hosts the Copilot runtime."),
            _ => throw new NotSupportedException($"Unknown endpoint kind '{model.Kind}' on endpoint '{model.EndpointName}'."),
        };

        return provider.AsBuilder()
            .UseOpenTelemetry(loggerFactory, ModelClientOptions.TelemetrySourceName, otel =>
            {
                if (options.Value.CaptureMessageContent is { } capture)
                {
                    otel.EnableSensitiveData = capture;
                }
            })
            .Use(inner => new ConcurrencyGateChatClient(inner, gates.For(model)))
            .Build();
    }

    private static IChatClient CreateOpenAI(ResolvedModel model)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(model.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(model.BaseUrl);
        }

        // Local servers such as Ollama ignore the key, but the SDK insists on one.
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(model.ApiKey) ? "no-key" : model.ApiKey);

        return new OpenAIClient(credential, clientOptions).GetChatClient(model.Model).AsIChatClient();
    }
}
