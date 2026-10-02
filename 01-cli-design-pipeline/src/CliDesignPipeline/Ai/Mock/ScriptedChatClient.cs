using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CliDesignPipeline.Ai.Mock;

/// <summary>Everything a mock script needs to decide what the "model" says next.</summary>
public sealed record MockTurn(string Agent, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, int CallNumber)
{
    public ChatMessage? Last => Messages is [.., var last] ? last : null;

    /// <summary>True when the previous assistant turn requested tools and these are their results.</summary>
    public bool HasToolResults => Last?.Role == ChatRole.Tool;

    /// <summary>The text of the most recent user message (the "prompt" for this turn).</summary>
    public string LastUserText => Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

    public int UserMessageCount => Messages.Count(m => m.Role == ChatRole.User);

    /// <summary>Results of tool calls made since the last user message.</summary>
    public IEnumerable<string> RecentToolResults => Messages
        .Reverse()
        .TakeWhile(m => m.Role != ChatRole.User)
        .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
        .Select(r => r.Result?.ToString() ?? "");
}

/// <summary>
/// An <see cref="IChatClient"/> that answers from a C# script instead of a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why mock at the IChatClient level?</b> It is the narrowest seam that still exercises the
/// whole stack: the agent, its context providers, structured-output parsing, the automatic
/// function-invocation loop (our scripted "model" really does return <see cref="FunctionCallContent"/>
/// and the real tools really run), and the workflow. Only the token generation is fake.
/// </para>
/// <para>
/// Uses: running the sample with no keys, deterministic demos, and unit tests of executors and
/// workflows (assert on routing and tool calls without paying for, or flaking on, a model).
/// </para>
/// <para>
/// Alternatives: record/replay real responses (a DelegatingChatClient that writes to disk on
/// first run and replays after - great for regression tests), or a small local model through
/// Ollama / Foundry Local, which is realistic but neither deterministic nor fast.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient(string agent, Func<MockTurn, ChatResponse> script) : IChatClient
{
    private int _calls;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // A small delay keeps the console output readable and makes it feel like a model.
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        var response = script(new MockTurn(agent, [.. messages], options, Interlocked.Increment(ref _calls)));
        response.ModelId ??= $"mock-{agent}";
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("mock", null, $"mock-{agent}")
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose() { }
}
