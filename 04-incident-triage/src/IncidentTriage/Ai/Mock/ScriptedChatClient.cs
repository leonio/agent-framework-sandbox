using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentTriage.Agents;
using IncidentTriage.Workflow;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Ai.Mock;

/// <summary>
/// An offline <see cref="IChatClient"/> that plays the part of the model for one named agent.
/// </summary>
/// <remarks>
/// <para><b>WHY not just return canned strings?</b> The interesting parts of an agentic workflow are the
/// plumbing around the model: tool-call loops, structured output parsing, RAG context injection,
/// human feedback flowing back into the next attempt. This mock exercises all of them for real:</para>
/// <list type="bullet">
///   <item>It answers with <see cref="FunctionCallContent"/> first, so <c>ChatClientAgent</c>'s
///     function-invoking layer actually runs our C# tools and sends the results back.</item>
///   <item>It reads the RAG context the <c>TextSearchProvider</c> injected and cites what it found, so if
///     retrieval breaks, the output visibly degrades.</item>
///   <item>It reads "REVIEWER FEEDBACK" in the conversation and changes its hypothesis.</item>
/// </list>
/// <para>It is heuristic (regexes over the prompt), so its "reasoning" is shallow by design. Switch
/// <c>Ai:Provider</c> to a real model to see the same workflow with real judgement.</para>
/// <para><b>Alternative:</b> record real model traffic once and replay it (a "VCR" client). Higher
/// fidelity, but breaks every time a prompt changes; a scripted fake survives prompt edits.</para>
/// </remarks>
public sealed partial class ScriptedChatClient(string agentName) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(120, cancellationToken); // a hint of latency so the console narration reads naturally
        var conversation = new Conversation([.. messages]);
        // Debugging aid: MOCK_TRACE_FILE=trace.txt shows exactly what each agent receives: its instructions (house
        // rules + prompt file + the skills advert added by AgentSkillsProvider), the RAG context TextSearchProvider
        // injected, and every tool call/result. The fastest way to understand what the framework does for you.
        if (Environment.GetEnvironmentVariable("MOCK_TRACE_FILE") is { Length: > 0 } traceFile)
            Trace(traceFile, conversation, options);

        var message = agentName switch
        {
            AgentNames.SignalExtractor => Json(ExtractSignals(conversation.LastUserText)),
            AgentNames.IncidentCorrelator => Json(Correlate(conversation.FirstJsonBlock<List<IncidentSignals>>() ?? [])),
            AgentNames.RootCauseAnalyst => conversation.ToolResultsSinceLastUser.Count == 0
                ? ToolCalls(conversation, ("search_code", new() { ["query"] = RcaCodeQuery(conversation) }), ("recent_commits", new() { ["maxCount"] = 10 }))
                : Json(Hypothesise(conversation)),
            AgentNames.JiraDrafter => conversation.ToolResultsSinceLastUser.Count == 0 && options?.Tools?.Any(t => t.Name == "search_jira_issues") == true
                ? ToolCalls(conversation, ("search_jira_issues", new() { ["text"] = JiraQuery(conversation) }))
                : Json(DraftJira(conversation)),
            AgentNames.PostmortemWriter => new ChatMessage(ChatRole.Assistant, WritePostmortem(conversation)),
            _ => new ChatMessage(ChatRole.Assistant, $"(mock) No script for agent '{agentName}'."),
        };

        return new ChatResponse(message) { ModelId = $"mock/{agentName}", FinishReason = ChatFinishReason.Stop };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted-mock", defaultModelId: agentName)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    // ============================================================================================
    // signal-extractor
    // ============================================================================================

    [GeneratedRegex(@"Report id:\s*(?<id>\S+)")] private static partial Regex ReportIdRx();
    [GeneratedRegex(@"\b(?<svc>[a-z][a-z0-9]*(?:-[a-z0-9]+)*-(?:api|service|svc|worker|gateway|db|web))\b")] private static partial Regex ServiceRx();
    [GeneratedRegex(@"\b[A-Z][A-Za-z]+(?:Exception|Error)\b(?::[^\n]{0,90})?")] private static partial Regex ExceptionRx();
    [GeneratedRegex(@"\b(?:HTTP\s*)?(?<code>5\d\d)\b")] private static partial Regex Http5xxRx();
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?Z?")] private static partial Regex TimestampRx();

    private static IncidentSignals ExtractSignals(string prompt)
    {
        var id = ReportIdRx().Match(prompt) is { Success: true } m ? m.Groups["id"].Value : "R?";
        var body = prompt.Contains("<<<", StringComparison.Ordinal) ? prompt[(prompt.IndexOf("<<<", StringComparison.Ordinal) + 3)..].Replace(">>>", "") : prompt;
        var lower = body.ToLowerInvariant();

        var service = ServiceRx().Matches(lower).Select(x => x.Groups["svc"].Value).GroupBy(x => x).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "unknown";

        List<string> signatures = [.. ExceptionRx().Matches(body).Select(x => x.Value.Trim()).Distinct().Take(3)];
        signatures.AddRange(Http5xxRx().Matches(body).Select(x => $"HTTP {x.Groups["code"].Value}").Distinct().Take(2));

        string[] symptomWords = ["latency", "timeout", "timed out", "error rate", "slow", "unavailable", "failed", "failing", "spike", "cache miss", "p99", "retries"];
        var symptoms = symptomWords.Where(lower.Contains).ToList();

        var firstSeen = TimestampRx().Matches(body).Select(x => x.Value).Order().FirstOrDefault() ?? "";

        var severity = lower switch
        {
            _ when lower.Contains("sev1") || lower.Contains("outage") || lower.Contains("all customers") => Severity.Sev1,
            _ when lower.Contains("sev2") || lower.Contains("5xx") || lower.Contains("payment") || signatures.Any(s => s.StartsWith("HTTP 5", StringComparison.Ordinal)) => Severity.Sev2,
            _ when symptoms.Count > 0 => Severity.Sev3,
            _ => Severity.Sev4,
        };

        var summary = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return new IncidentSignals(id, service, Truncate(summary, 160), signatures, symptoms, firstSeen, severity);
    }

    // ============================================================================================
    // incident-correlator
    // ============================================================================================

    private static CorrelationResult Correlate(List<IncidentSignals> signals) => new(
        [.. signals
            .GroupBy(s => s.Service)
            .OrderBy(g => g.Min(s => s.Severity))
            .Select((g, i) => new IncidentCluster(
                ClusterId: $"INC-{i + 1}",
                Title: $"{g.Key}: {Truncate(g.OrderBy(s => s.Severity).First().Summary, 90)}",
                Service: g.Key,
                ReportIds: [.. g.Select(s => s.ReportId)],
                Severity: g.Min(s => s.Severity),
                Rationale: g.Count() == 1
                    ? "Single report for this service; no other report shares its service or signatures."
                    : $"{g.Count()} reports name {g.Key} and share signatures ({string.Join(", ", g.SelectMany(s => s.ErrorSignatures).Distinct().Take(3))})."))]);

    // ============================================================================================
    // root-cause-analyst
    // ============================================================================================

    [GeneratedRegex(@"\[(?<kind>runbook|postmortem):(?<file>[^\]]+)\]\s*(?<title>[^\n]*)\n(?<body>(?:(?!\n\[(?:runbook|postmortem):).)*)", RegexOptions.Singleline)]
    private static partial Regex KnowledgeChunkRx();
    [GeneratedRegex(@"code:(?<path>[^\s:]+):(?<line>\d+)")] private static partial Regex CodeCitationRx();
    // Matches `git log --pretty="%h %ad %an: %s"` lines: sha, date, author, subject.
    [GeneratedRegex(@"^(?<sha>[0-9a-f]{7,40})\s+\d{4}-\d{2}-\d{2}\s+[^:\n]+:\s+(?<rest>.+)$", RegexOptions.Multiline)] private static partial Regex CommitRx();
    // Type.Member identifiers from stack traces, e.g. "OrderRepository.SaveLineItemAsync".
    [GeneratedRegex(@"\b(?<type>[A-Z][A-Za-z0-9]+)\.(?<member>[A-Z][A-Za-z0-9]+)\(")] private static partial Regex StackFrameRx();

    private static string RcaCodeQuery(Conversation c)
    {
        var cluster = c.FirstJsonBlock<IncidentCluster>();
        var signals = c.JsonBlocks<List<IncidentSignals>>().FirstOrDefault() ?? [];
        // What a good engineer (or model) does: search for OUR code named in stack traces, not for generic error text.
        var frames = StackFrameRx().Matches(c.AllUserText)
            .Where(m => !m.Value.StartsWith("Microsoft.", StringComparison.Ordinal) && !m.Value.StartsWith("System.", StringComparison.Ordinal))
            .Select(m => $"{m.Groups["type"].Value} {m.Groups["member"].Value}")
            .Distinct().Take(3);
        string[] terms = [cluster?.Service ?? "", .. frames, .. signals.SelectMany(s => s.ErrorSignatures).Where(s => !s.StartsWith("HTTP", StringComparison.Ordinal)).Select(s => s.Split(':')[0]).Distinct().Take(2)];
        return string.Join(' ', terms.Where(t => t.Length > 0));
    }

    private static RootCauseHypothesis Hypothesise(Conversation c)
    {
        var cluster = c.FirstJsonBlock<IncidentCluster>();
        var round = Regex.Matches(c.AllUserText, "REVIEWER FEEDBACK").Count; // 0 on the first attempt

        // RAG context injected by TextSearchProvider (see AgentFactory) is formatted as "[runbook:file] # Title\n...sections...".
        var chunks = KnowledgeChunkRx().Matches(c.AllText)
            .Select(m => (Kind: m.Groups["kind"].Value, File: m.Groups["file"].Value.Trim(), Title: m.Groups["title"].Value.TrimStart('#', ' '), Body: m.Groups["body"].Value))
            .Where(k => k.File.Length > 0)
            .ToList();

        // Like a model would: prefer the runbook that matches the service named in the cluster.
        var service = cluster?.Service ?? "the service";
        var runbookDocs = chunks.Where(k => k.Kind == "runbook").OrderByDescending(k => k.Body.Contains(service, StringComparison.OrdinalIgnoreCase)).ToList();
        var causes = runbookDocs.SelectMany(k => Bullets(Section(k.Body, "Likely causes"))).Distinct().ToList();
        var mitigations = runbookDocs.Take(1).SelectMany(k => Bullets(Section(k.Body, "Mitigation"))).Distinct().Take(4).ToList();
        var runbooks = runbookDocs.Select(k => k.File).Distinct().Take(2).ToList();
        var postmortem = chunks.FirstOrDefault(k => k.Kind == "postmortem");

        var toolText = string.Join('\n', c.ToolResultsSinceLastUser);
        // First hit that is actual source code (README / config hits are context, not the culprit).
        var code = CodeCitationRx().Matches(toolText).FirstOrDefault(m => m.Groups["path"].Value.EndsWith(".cs", StringComparison.Ordinal) || m.Groups["path"].Value.EndsWith(".ts", StringComparison.Ordinal) || m.Groups["path"].Value.EndsWith(".py", StringComparison.Ordinal))
            ?? CodeCitationRx().Match(toolText);
        var commits = CommitRx().Matches(toolText).Select(m => (Sha: m.Groups["sha"].Value, Text: m.Groups["rest"].Value)).ToList();
        var serviceWords = service.Split('-');
        var suspectCommit = commits.FirstOrDefault(cm => serviceWords.Any(w => w.Length > 3 && cm.Text.Contains(w, StringComparison.OrdinalIgnoreCase))
                                                        || (code.Success && cm.Text.Contains(Path.GetFileNameWithoutExtension(code.Groups["path"].Value), StringComparison.OrdinalIgnoreCase)));

        // Each rejection moves to the next "likely cause" from the runbook. Real models do something
        // similar but smarter: the rejected answer and the reviewer's reason are in the session history.
        var cause = causes.Count > 0 ? causes[Math.Min(round, causes.Count - 1)] : "insufficient evidence in runbooks; needs manual investigation";
        var useCommit = round == 0 && suspectCommit.Sha is not null;
        var sha = suspectCommit.Sha is { } full ? full[..Math.Min(7, full.Length)] : "";

        List<Evidence> evidence = [];
        if (cluster is not null) evidence.Add(new($"reports:{string.Join(",", cluster.ReportIds)}", cluster.Rationale));
        if (runbooks.Count > 0) evidence.Add(new($"runbook:{runbooks[0]}", $"Runbook lists \"{Truncate(cause, 100)}\" as a likely cause for these symptoms."));
        if (code.Success) evidence.Add(new($"code:{code.Groups["path"].Value}:{code.Groups["line"].Value}", "Code search matched the failing path for the reported signatures."));
        if (useCommit) evidence.Add(new($"commit:{suspectCommit.Sha}", $"Recent change touching this area: {Truncate(suspectCommit.Text, 120)}"));
        if (postmortem.File is not null) evidence.Add(new($"postmortem:{postmortem.File}", $"A past incident with similar signals: {Truncate(postmortem.Title, 100)}"));

        return new RootCauseHypothesis(
            Summary: useCommit
                ? $"{service}: {cause}, most likely introduced by commit {sha} (\"{Truncate(suspectCommit.Text, 70)}\")."
                : $"{service}: {cause}.",
            Category: useCommit ? "deploy regression" : round > 0 ? "capacity / dependency" : "unknown",
            SuspectedComponent: useCommit ? $"commit {sha}" : code.Success ? code.Groups["path"].Value : service,
            Confidence: Math.Round(useCommit ? 0.72 : Math.Max(0.35, 0.6 - 0.1 * round), 2),
            Evidence: evidence,
            Mitigations: mitigations.Count > 0 ? mitigations : ["Page the owning team and follow the generic incident runbook."],
            RunbookReferences: runbooks);
    }

    // ============================================================================================
    // jira-drafter
    // ============================================================================================

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]+-\d+\b")] private static partial Regex IssueKeyRx();

    private static string JiraQuery(Conversation c) =>
        c.FirstJsonBlock<ReviewedIncident>() is { } i ? $"{i.Cluster.Service} {i.Hypothesis.Category}" : "incident";

    private static JiraIssueDraft DraftJira(Conversation c)
    {
        var incident = c.FirstJsonBlock<ReviewedIncident>() ?? throw new InvalidOperationException("mock jira-drafter expected a ReviewedIncident JSON block");
        var h = incident.Hypothesis;
        var related = IssueKeyRx().Matches(string.Join('\n', c.ToolResultsSinceLastUser)).Select(m => m.Value).Distinct().Take(3).ToList();

        var description = new StringBuilder();
        if (incident.FinalVerdict != ReviewVerdict.Accept)
            description.AppendLine($"ROOT CAUSE UNCONFIRMED: reviewer verdict was {incident.FinalVerdict} ({incident.History.LastOrDefault()?.Decision.Reason}).").AppendLine();
        description
            .AppendLine($"Impact: {incident.Cluster.Title} ({incident.Cluster.Severity}). Reports: {string.Join(", ", incident.Cluster.ReportIds)}.")
            .AppendLine().AppendLine($"Root cause (confidence {h.Confidence:P0}, reviewed: {incident.FinalVerdict}): {h.Summary}")
            .AppendLine().AppendLine("Evidence:");
        foreach (var e in h.Evidence) description.AppendLine($"- [{e.Source}] {e.Detail}");
        description.AppendLine().AppendLine("Mitigation:");
        foreach (var m in h.Mitigations) description.AppendLine($"- {m}");
        description.AppendLine().AppendLine("Follow-ups: add a regression test for the suspected component; add an alert on the leading signal.");

        return new JiraIssueDraft(
            ClusterId: incident.Cluster.ClusterId,
            Summary: Truncate(h.Summary.StartsWith(incident.Cluster.Service, StringComparison.Ordinal) ? h.Summary : $"{incident.Cluster.Service}: {h.Summary}", 120),
            Description: description.ToString().Trim(),
            IssueType: incident.Cluster.Severity <= Severity.Sev2 ? "Incident" : "Bug",
            Priority: incident.Cluster.Severity switch { Severity.Sev1 => JiraPriority.Highest, Severity.Sev2 => JiraPriority.High, Severity.Sev3 => JiraPriority.Medium, _ => JiraPriority.Low },
            Labels: ["incident", incident.Cluster.Service, .. h.Category.Split(' ', '/').Where(w => w.Length > 2).Select(w => w.ToLowerInvariant())],
            RelatedIssueKeys: related);
    }

    // ============================================================================================
    // postmortem-writer
    // ============================================================================================

    private static string WritePostmortem(Conversation c)
    {
        var incident = c.FirstJsonBlock<ReviewedIncident>() ?? throw new InvalidOperationException("mock postmortem-writer expected a ReviewedIncident JSON block");
        var h = incident.Hypothesis;
        var sb = new StringBuilder()
            .AppendLine($"# Postmortem: {incident.Cluster.Title}").AppendLine()
            .AppendLine("> Status: DRAFT generated by the incident-triage workflow. Blameless format: describe systems, not people.").AppendLine()
            .AppendLine("## Summary").AppendLine(h.Summary).AppendLine()
            .AppendLine("## Impact").AppendLine($"Severity {incident.Cluster.Severity}. Affected service: {incident.Cluster.Service}. Reports: {string.Join(", ", incident.Cluster.ReportIds)}.").AppendLine()
            .AppendLine("## Timeline");
        // Only timestamps that appear in the raw reports; nothing invented (house rules).
        foreach (var line in c.AllUserText.Split('\n').Where(l => TimestampRx().IsMatch(l) && !l.TrimStart().StartsWith('{')).Select(l => l.Trim()).Distinct().OrderBy(l => TimestampRx().Match(l).Value).Take(8))
            sb.AppendLine($"- {Truncate(line, 140)}");
        sb.AppendLine()
            .AppendLine("## Root cause").AppendLine($"{h.Summary} (category: {h.Category}, confidence {h.Confidence:P0}).").AppendLine()
            .AppendLine("## Evidence");
        foreach (var e in h.Evidence) sb.AppendLine($"- **{e.Source}**: {e.Detail}");
        sb.AppendLine().AppendLine("## Mitigation");
        foreach (var m in h.Mitigations) sb.AppendLine($"- {m}");
        sb.AppendLine().AppendLine("## Review history");
        foreach (var r in incident.History) sb.AppendLine($"- Round {r.Round}: {r.Decision.Verdict} by {r.Decision.Reviewer}{(string.IsNullOrWhiteSpace(r.Decision.Reason) ? "" : $" ({r.Decision.Reason})")}");
        sb.AppendLine().AppendLine("## Action items")
          .AppendLine("| Action | Type | Owner |").AppendLine("|---|---|---|")
          .AppendLine($"| Fix {h.SuspectedComponent} | Prevent | TBD |")
          .AppendLine($"| Alert on the leading signal for {incident.Cluster.Service} | Detect | TBD |")
          .AppendLine($"| Update runbook {string.Join(", ", h.RunbookReferences.DefaultIfEmpty("(new runbook)"))} | Mitigate | TBD |");
        return sb.ToString();
    }

    // ============================================================================================
    // helpers
    // ============================================================================================

    private static readonly Lock TraceGate = new();

    private void Trace(string path, Conversation c, ChatOptions? options)
    {
        var sb = new StringBuilder().AppendLine($"\n==================== {agentName} ====================")
            .AppendLine($"[instructions]\n{options?.Instructions}")
            .AppendLine($"[tools] {string.Join(", ", options?.Tools?.Select(t => t.Name) ?? [])}");
        foreach (var m in c.Messages)
        {
            sb.AppendLine($"[{m.Role}]");
            foreach (var content in m.Contents)
                sb.AppendLine(content switch
                {
                    TextContent t => t.Text,
                    FunctionCallContent call => $"CALL {call.Name}({string.Join(", ", call.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])})",
                    FunctionResultContent result => $"RESULT {result.Result}",
                    _ => content.GetType().Name,
                });
        }
        lock (TraceGate) File.AppendAllText(path, sb.ToString());
    }

    private static ChatMessage Json<T>(T value) => new(ChatRole.Assistant, JsonSerializer.Serialize(value, JsonDefaults.Compact));

    private static ChatMessage ToolCalls(Conversation c, params ReadOnlySpan<(string Name, Dictionary<string, object?> Args)> calls)
    {
        List<AIContent> contents = [];
        foreach (var (name, args) in calls)
            contents.Add(new FunctionCallContent($"call_{name}_{c.Messages.Count}", name, args));
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static IEnumerable<string> Bullets(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal) || Regex.IsMatch(l, @"^\d+\.\s")).Select(l => Regex.Replace(l, @"^(-|\d+\.)\s+", "").Trim().TrimEnd('.'));

    /// <summary>The text under a "## heading" in a markdown document, up to the next "## ".</summary>
    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf($"## {heading}", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        var end = markdown.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Read-only helpers over the messages the agent sent us.</summary>
    private sealed partial class Conversation(List<ChatMessage> messages)
    {
        [GeneratedRegex(@"```json\s*(?<json>.*?)```", RegexOptions.Singleline)] private static partial Regex JsonBlockRx();

        public List<ChatMessage> Messages => messages;

        public string LastUserText => messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        public string AllUserText => string.Join('\n', messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));

        public string AllText => string.Join('\n', messages.Select(m => m.Text));

        /// <summary>Tool results that arrived after the most recent user turn (i.e. in this tool loop).</summary>
        public List<string> ToolResultsSinceLastUser =>
        [
            .. messages.Skip(messages.FindLastIndex(m => m.Role == ChatRole.User) + 1)
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Select(r => r.Result?.ToString() ?? ""),
        ];

        public IEnumerable<T> JsonBlocks<T>() where T : class
        {
            foreach (Match m in JsonBlockRx().Matches(AllUserText))
            {
                T? value = null;
                try { value = JsonSerializer.Deserialize<T>(m.Groups["json"].Value, JsonDefaults.Options); }
                catch (JsonException) { /* not this shape; try the next block */ }
                if (value is not null) yield return value;
            }
        }

        public T? FirstJsonBlock<T>() where T : class => JsonBlocks<T>().FirstOrDefault();
    }
}
