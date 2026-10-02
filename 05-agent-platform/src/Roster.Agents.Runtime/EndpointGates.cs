using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Roster.Agents.Runtime;

/// <summary>
/// One semaphore per model endpoint, shared by every agent call in this process. It caps how many requests are in
/// flight against an endpoint at once (<see cref="ResolvedModel.MaxConcurrency"/>), so three reviewers running in
/// parallel, times several assignments, do not trip a provider's rate limit or swamp a local Ollama.
/// </summary>
/// <remarks>
/// The cap is per process (per runner replica) in slice 1. A shared counter in Postgres can make it global later; the
/// design doc lists that as open. Registered as a singleton.
/// </remarks>
public sealed class EndpointGates
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>
    /// The semaphore for <paramref name="model"/>'s endpoint. The key includes the capacity, so if an admin changes the
    /// limit, new calls use a fresh semaphore of the new size while calls holding the old one finish normally.
    /// </summary>
    public SemaphoreSlim For(ResolvedModel model)
    {
        int capacity = Math.Max(1, model.MaxConcurrency);
        string key = $"{model.EndpointId?.ToString() ?? model.EndpointName}|{capacity}";
        return _gates.GetOrAdd(key, _ => new SemaphoreSlim(capacity, capacity));
    }
}

/// <summary>
/// Waits for a slot on the endpoint's semaphore before each model call and gives it back when the call ends. Sits
/// below the function-invoking layer that <c>ChatClientAgent</c> adds, so a slot is held for one model round trip and
/// released while tools run.
/// </summary>
internal sealed class ConcurrencyGateChatClient(IChatClient inner, SemaphoreSlim gate) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A streaming call holds its slot until the last update has been read (or the caller stops reading).
        await gate.WaitAsync(cancellationToken);
        try
        {
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
