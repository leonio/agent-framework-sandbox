using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MrArchitectureReview.Ai;

/// <summary>
/// An offline <see cref="IChatClient"/> that replays a script from <c>MockResponses/{agent}.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY build a fake model at all? Three reasons:
/// <list type="number">
///   <item>The sample runs end to end with zero setup (no keys, no network, no cost).</item>
///   <item>Runs are deterministic, so the workflow, persistence and human-in-the-loop logic can be
///         unit-tested. LLM output is the one part you cannot assert on exactly.</item>
///   <item>It shows how thin the model boundary is: everything above <c>IChatClient</c>
///         (agents, tools, context providers, workflows) runs for real.</item>
/// </list>
/// </para>
/// <para>
/// The script can make the "model" request tool calls first. The agent's automatic function
/// invocation then really executes our C# tools (reading files from the cloned PR, calling the
/// dry-run Jira client, ...) and feeds the results back, exactly as with a real model.
/// </para>
/// <para>
/// Script format:
/// <code>
/// {
///   "toolCalls": [ { "name": "read_file", "arguments": { "path": "src/Foo.cs" } } ],
///   "response":  { ...any JSON... }   // or a plain string
/// }
/// </code>
/// </para>
/// <para>
/// Alternatives: record/replay of real traffic (e.g. a caching <c>IChatClient</c> middleware backed by
/// files), or a local model via Ollama / Foundry Local. Both are more realistic but neither is
/// deterministic and both need extra installs.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient(string agentName, string scriptDirectory, ILogger<ScriptedChatClient> logger) : IChatClient
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var script = await LoadScriptAsync(cancellationToken);
        var history = messages as IList<ChatMessage> ?? [.. messages];

        // Show what the agent pipeline actually sent: the instructions (system prompt + anything context
        // providers added) and the messages (user prompt + RAG results). Run with
        // --Logging:LogLevel:MrArchitectureReview.Ai=Debug to see it.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("[{Agent}] instructions:\n{Instructions}", agentName, options?.Instructions);
            foreach (var m in history.Where(m => m.Role != ChatRole.Tool))
                logger.LogDebug("[{Agent}] {Role} message ({Length} chars): {Preview}", agentName, m.Role, m.Text.Length,
                    m.Text.Length > 300 ? m.Text[..300] + "..." : m.Text);
        }

        // Simulate a little latency so the concurrent fan-out is visible in the timings.
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);

        // Have we already asked for tools in this conversation? If not, and the script has some, ask now.
        var alreadyCalledTools = history.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        var availableTools = options?.Tools?.Select(t => t.Name).ToHashSet() ?? [];

        if (!alreadyCalledTools && script["toolCalls"] is JsonArray calls && calls.Count > 0)
        {
            List<AIContent> requested = [];
            foreach (var (call, index) in calls.Select((c, i) => (c!, i)))
            {
                var name = call["name"]!.GetValue<string>();
                if (!availableTools.Contains(name))
                {
                    // A real model can only call tools it was given. Mirror that so a stale script is obvious.
                    logger.LogWarning("[{Agent}] script asks for tool '{Tool}' which the agent does not have; skipping", agentName, name);
                    continue;
                }

                var args = call["arguments"]?.Deserialize<Dictionary<string, object?>>(s_json) ?? [];
                requested.Add(new FunctionCallContent($"call_{agentName}_{index}", name, args));
            }

            if (requested.Count > 0)
            {
                logger.LogDebug("[{Agent}] scripted model requests {Count} tool call(s)", agentName, requested.Count);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, requested)) { ModelId = "scripted" };
            }
        }

        var text = script["response"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonNode node => node.ToJsonString(s_json),
            null => "",
        };

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "scripted" };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming is just the non-streaming response chopped into updates. Good enough for a mock.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    private async Task<JsonNode> LoadScriptAsync(CancellationToken ct)
    {
        var path = Path.Combine(scriptDirectory, $"{agentName}.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No mock script for agent '{agentName}'. Create {path} or configure a real provider.", path);

        await using var stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct)
               ?? throw new InvalidDataException($"Empty mock script: {path}");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
