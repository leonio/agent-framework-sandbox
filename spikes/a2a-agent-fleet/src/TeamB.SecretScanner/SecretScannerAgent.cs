using System.Text.Json;
using System.Text.RegularExpressions;
using FleetSpike.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace TeamB.SecretScanner;

/// <summary>
/// An agent with no model in it. Team B's scanner is a handful of regexes over the added lines of a diff, wrapped in the
/// <see cref="AIAgent"/> shape so it can sit behind A2A like any other agent. The platform treats it identically to an
/// LLM-backed reviewer: same contract, same card, same call.
/// </summary>
internal sealed partial class SecretScannerAgent : AIAgent
{
    public override string? Name => "secret-scanner";

    public override string? Description => "Finds committed secrets in the added lines of a diff using fixed rules. No model.";

    private sealed class ScannerSession : AgentSession
    {
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new ScannerSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(new ScannerSession());

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string text = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        Findings result = Scan(text);
        string json = JsonSerializer.Serialize(result, ContractJson.Options);
        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text);
    }

    private static Findings Scan(string inputJson)
    {
        ChangeReviewInput? input;
        try
        {
            input = JsonSerializer.Deserialize<ChangeReviewInput>(inputJson, ContractJson.Options);
        }
        catch (JsonException)
        {
            input = null;
        }

        if (input is null)
        {
            return new Findings(
                "The input did not match the published contract, so nothing was scanned.",
                [new Finding("Input does not match contract", "Expected a ChangeReviewInput JSON document.",
                    "Send the input described by /.well-known/agent-contract.json.", Severity.Info, null, 1.0)]);
        }

        List<Finding> items = [];
        string? path = null;
        foreach (string line in input.Diff.ReplaceLineEndings("\n").Split('\n'))
        {
            Match header = FileHeader().Match(line);
            if (header.Success)
            {
                path = header.Groups["path"].Value;
                continue;
            }

            // Only added lines: a secret that was removed is not this change's problem.
            if (!line.StartsWith('+') || line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((string title, Regex rule, Severity severity) in Rules)
            {
                if (rule.IsMatch(line))
                {
                    items.Add(new Finding(
                        title,
                        "A value that looks like a credential is added in this change.",
                        "Remove it, rotate it, and load it from a secret store or user-secrets.",
                        severity, path, 0.9));
                }
            }
        }

        string summary = items.Count == 0 ? "No secret patterns in the added lines." : $"{items.Count} possible secret(s) in added lines.";
        return new Findings(summary, [.. items.OrderByDescending(i => i.Severity).Take(5)]);
    }

    private static readonly (string Title, Regex Rule, Severity Severity)[] Rules =
    [
        ("AWS access key id", AwsKey(), Severity.High),
        ("Private key material", PrivateKey(), Severity.High),
        ("GitHub token", GitHubToken(), Severity.High),
        ("Hard-coded credential assignment", Assignment(), Severity.Medium),
        ("Password in connection string", ConnectionString(), Severity.Medium),
    ];

    [GeneratedRegex(@"^\+\+\+ b/(?<path>\S+)")]
    private static partial Regex FileHeader();

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{36,}")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?i)(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*[""'][^""']{6,}[""']")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(?i)(password|pwd)=[^;""']{4,};")]
    private static partial Regex ConnectionString();
}
