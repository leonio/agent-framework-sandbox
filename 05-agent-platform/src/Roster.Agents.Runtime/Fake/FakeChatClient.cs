using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Roster.Agents.Runtime.Fake;

/// <summary>
/// The <c>fake</c> endpoint kind: an <see cref="IChatClient"/> that answers without a model, so scenarios can run
/// offline, in demos and at scale. It is a dev tool, not a test double, and it never pretends to be a model: its text
/// says it is the fake.
/// </summary>
/// <remarks>
/// <para>What it answers, in order:</para>
/// <list type="number">
/// <item><b>A typed request</b> (a JSON schema in <c>ResponseFormat</c>, or the prompted-JSON instructions from
/// <see cref="StructuredOutput"/>): the three reviewers get <see cref="FakeReviewers"/> findings; any other agent gets a
/// <see cref="SchemaSampler"/> value. Prompted requests get the JSON inside a markdown fence, the way real models often
/// reply, so the runner's extraction is exercised too.</item>
/// <item><b>A conversation with tools</b>: <see cref="FakeFacilitator"/>'s script.</item>
/// <item><b>A conversation without tools</b>: a polite note that this is the fake endpoint.</item>
/// </list>
/// <para>The model name picks a flavour: <c>fake</c> (default), <c>fake-flaky</c> (the first typed reply in a
/// conversation is cut-off JSON, to exercise the runner's repair attempt), <c>fake-slow</c> (a couple of seconds per
/// call, for watching streaming and concurrency).</para>
/// <para>Replies carry rough token counts (four characters a token) so ledger rows and scorecards have numbers, and
/// typed replies carry a short reasoning note as <see cref="TextReasoningContent"/>, so reasoning capture and the
/// retro's <c>get_reasoning</c> have something to show.</para>
/// </remarks>
internal sealed class FakeChatClient(string agentName, string model) : IChatClient
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    private bool IsFlaky => model.Equals("fake-flaky", StringComparison.OrdinalIgnoreCase);

    private TimeSpan Latency => model.Equals("fake-slow", StringComparison.OrdinalIgnoreCase)
        ? TimeSpan.FromSeconds(2)
        : TimeSpan.FromMilliseconds(150);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> history = [.. messages];
        await Task.Delay(Latency, cancellationToken);

        ChatMessage reply = Answer(history, options);
        return new ChatResponse(reply)
        {
            ModelId = model,
            FinishReason = reply.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = EstimateUsage(history, options, reply),
        };
    }

    /// <summary>
    /// Streams the same answer: text in small chunks a few words long, reasoning and tool calls as single updates, and
    /// the usage at the end, which is roughly how real endpoints stream.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        string responseId = $"fake_{Guid.NewGuid():N}";

        foreach (AIContent content in response.Messages.SelectMany(m => m.Contents))
        {
            if (content is TextContent text)
            {
                foreach (string chunk in Chunks(text.Text, wordsPerChunk: 3))
                {
                    await Task.Delay(25, cancellationToken);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { ResponseId = responseId, ModelId = model };
                }
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { ResponseId = responseId, ModelId = model };
            }
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)])
        {
            ResponseId = responseId,
            ModelId = model,
            FinishReason = response.FinishReason,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", defaultModelId: model)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private ChatMessage Answer(List<ChatMessage> history, ChatOptions? options)
    {
        if (TryGetSchema(history, options, out JsonElement schema, out bool prompted))
        {
            return AnswerTyped(history, schema, prompted);
        }

        if (options?.Tools is { Count: > 0 })
        {
            return FakeFacilitator.Next(history, options.Tools);
        }

        return new ChatMessage(ChatRole.Assistant,
            "This is the fake endpoint, so there is no model behind this reply. Pick a real endpoint to get real answers.");
    }

    private ChatMessage AnswerTyped(List<ChatMessage> history, JsonElement schema, bool prompted)
    {
        // The task is in the first user message; later user messages are repair requests from the runner.
        string input = history.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        string reasoning;
        JsonNode? value;
        if (FakeReviewers.Handles(agentName))
        {
            value = FakeReviewers.Review(agentName, input, out reasoning);
        }
        else
        {
            value = SchemaSampler.Sample(schema);
            reasoning = $"Fake reasoning: no heuristics for {agentName}, so the reply is a sample that fits the schema.";
        }

        string json = value?.ToJsonString(s_indented) ?? "{}";

        // fake-flaky: the first typed reply in a conversation stops halfway, like a model hitting its token limit.
        bool firstReply = !history.Any(m => m.Role == ChatRole.Assistant);
        if (IsFlaky && firstReply)
        {
            json = json[..(json.Length / 2)];
            reasoning += " (fake-flaky: this reply is deliberately cut off.)";
        }

        string text = prompted ? $"Here is the result.\n\n```json\n{json}\n```" : json;
        return new ChatMessage(ChatRole.Assistant, [new TextReasoningContent(reasoning), new TextContent(text)]);
    }

    // Native structured output puts the schema in ResponseFormat. The prompted strategy pastes it into the
    // instructions (or, failing that, a system or user message), after StructuredOutput.PromptedMarker.
    private static bool TryGetSchema(List<ChatMessage> history, ChatOptions? options, out JsonElement schema, out bool prompted)
    {
        prompted = false;
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } native })
        {
            schema = native;
            return true;
        }

        IEnumerable<string> texts = new[] { options?.Instructions ?? "" }
            .Concat(history.Where(m => m.Role == ChatRole.System || m.Role == ChatRole.User).Select(m => m.Text));
        foreach (string text in texts)
        {
            if (StructuredOutput.TryReadPromptedSchema(text, out schema))
            {
                prompted = true;
                return true;
            }
        }

        schema = default;
        return false;
    }

    private static UsageDetails EstimateUsage(List<ChatMessage> history, ChatOptions? options, ChatMessage reply)
    {
        long input = ((options?.Instructions?.Length ?? 0) + history.Sum(m => m.Text.Length)) / 4;
        long output = reply.Text.Length / 4 + 1;
        return new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output };
    }

    // Splits text into pieces of a few words, keeping the spaces so the pieces join back to the original.
    private static IEnumerable<string> Chunks(string text, int wordsPerChunk)
    {
        int start = 0, words = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' && ++words == wordsPerChunk)
            {
                yield return text[start..(i + 1)];
                start = i + 1;
                words = 0;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
