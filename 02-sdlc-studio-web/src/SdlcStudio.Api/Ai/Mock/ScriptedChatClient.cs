using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SdlcStudio.Api.Ai.Mock;

/// <summary>
/// An offline <see cref="IChatClient"/> that returns scripted, but input-aware, answers for each agent.
/// Selected with <c>Llm:Provider = Mock</c> (the default), so the whole studio runs with no API keys.
/// </summary>
/// <remarks>
/// <para>
/// WHY mock at the IChatClient level rather than mocking agents or workflows?
/// Everything above the chat client stays real: the Agent Framework builds the prompts, attaches tools,
/// runs the function-invocation loop, applies the JSON response schema and deserializes structured output.
/// The mock even emits <see cref="FunctionCallContent"/> so the tool calling path is exercised offline.
/// </para>
/// <para>
/// ALTERNATIVES: record/replay real model traffic (deterministic and realistic, but needs keys once),
/// or a small local model through Ollama / Foundry Local (real but slower and non-deterministic).
/// </para>
/// </remarks>
public sealed class ScriptedChatClient(string agentKey) : IChatClient
{
    /// <summary>Simulated model latency so the UI shows progress. Tests set this to zero.</summary>
    public static TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(350);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, cancellationToken);

        var reply = MockScripts.Respond(agentKey, [.. messages], options);
        return new ChatResponse(reply) { ModelId = "scripted-mock" };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
