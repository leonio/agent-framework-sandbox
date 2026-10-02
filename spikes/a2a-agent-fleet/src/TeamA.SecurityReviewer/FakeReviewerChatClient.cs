using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using FleetSpike.Shared;
using Microsoft.Extensions.AI;

namespace TeamA.SecurityReviewer;

/// <summary>
/// Stands in for the model. A real team builds an OpenAI-compatible <see cref="IChatClient"/> here and the rest of the
/// service does not change. This one reads the diff in the prompt and returns schema-valid JSON, so the spike runs with
/// no key and the structured-output path (response format set when the agent is created) is the real one.
/// </summary>
internal sealed partial class FakeReviewerChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        string userMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        Findings result = Review(RenderPrompt(userMessage));
        string json = JsonSerializer.Serialize(result, ContractJson.Options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>
    /// The caller sends the typed input as JSON. A real team renders it into a prompt (fenced as untrusted); the fake
    /// does the same so the diff has real newlines in it, which is also what a model would want to read.
    /// </summary>
    private static string RenderPrompt(string userMessage)
    {
        ChangeReviewInput? input;
        try
        {
            input = JsonSerializer.Deserialize<ChangeReviewInput>(userMessage, ContractJson.Options);
        }
        catch (JsonException)
        {
            input = null;
        }

        return input is null
            ? userMessage
            : $"Files: {string.Join(", ", input.Files)}\n<untrusted_pull_request>\nTitle: {input.Title}\n{input.Description}\n{input.Diff}\n</untrusted_pull_request>";
    }

    private static Findings Review(string prompt)
    {
        List<Finding> items = [];

        if (SqlConcat().IsMatch(prompt))
        {
            items.Add(new Finding(
                "SQL built by string concatenation",
                "A query is assembled from user input with + or interpolation, which allows SQL injection.",
                "Use parameterised queries.",
                Severity.High, FirstPath(prompt), 0.82));
        }

        if (AnonymousEndpoint().IsMatch(prompt))
        {
            items.Add(new Finding(
                "Endpoint opts out of authorisation",
                "A new endpoint is marked [AllowAnonymous] while its neighbours require authorisation.",
                "Require authorisation, or document why this endpoint must be public and get a security review.",
                Severity.Medium, FirstPath(prompt), 0.64));
        }

        if (NewHttpClient().IsMatch(prompt))
        {
            items.Add(new Finding(
                "HttpClient created per call",
                "Creating HttpClient per request exhausts sockets under load.",
                "Inject IHttpClientFactory or a typed client.",
                Severity.Low, FirstPath(prompt), 0.58));
        }

        string summary = items.Count == 0
            ? "Nothing material at a surface level."
            : $"{items.Count} issue(s) worth a human look, most important first.";
        return new Findings(summary, [.. items.OrderByDescending(i => i.Severity)]);
    }

    private static string? FirstPath(string prompt)
    {
        Match m = DiffHeader().Match(prompt);
        return m.Success ? m.Groups["path"].Value : null;
    }

    [GeneratedRegex(@"(?i)(select|insert|update|delete)\b[^\n""]*""\s*\+|\$""[^""\n]*(select|insert|update|delete)\b")]
    private static partial Regex SqlConcat();

    [GeneratedRegex(@"\[AllowAnonymous\]")]
    private static partial Regex AnonymousEndpoint();

    [GeneratedRegex(@"new\s+HttpClient\s*\(")]
    private static partial Regex NewHttpClient();

    [GeneratedRegex(@"^\+\+\+ b/(?<path>\S+)", RegexOptions.Multiline)]
    private static partial Regex DiffHeader();
}
