using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Roster.Agents.Runtime.Fake;

/// <summary>
/// Pattern-matching stand-ins for the three library reviewers, so the PR-review scenario produces believable findings
/// with no model at all. Each reviewer has a handful of rules for its own lens; a rule that matches an added line of the
/// diff (or, for a few, any part of the input) becomes one finding, quoting the line as evidence.
/// </summary>
/// <remarks>
/// <para>This is a dev tool, not a reviewer. It knows the reviewer agents by name and writes JSON in the shape of the
/// library's <c>Findings</c> contract (summary, items with title, detail, recommendation, severity, filePath,
/// confidence). If that contract changes, change the field names here too; the runner's schema validation will say so
/// loudly if they drift.</para>
/// <para>The rules are tuned to catch what the bundled sample pull request contains (SQL built by interpolation, a
/// logged card number, a committed live key, text that tries to instruct reviewers, a per-call HttpClient, data access
/// in a controller, a switch on provider, hard-coded URLs), and work on any diff.</para>
/// </remarks>
internal static class FakeReviewers
{
    /// <summary>Where a rule looks: only lines the diff adds, or the whole rendered input (title, description, diff).</summary>
    private enum Scope { AddedLines, WholeInput }

    private sealed record Rule(
        string Title,
        Regex Pattern,
        string Detail,
        string Recommendation,
        string Severity,
        double Confidence,
        Scope Scope = Scope.AddedLines,
        Regex? FileFilter = null);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Dictionary<string, (string Lens, Rule[] Rules)> s_reviewers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["security-reviewer"] = ("security",
        [
            new("SQL built by string interpolation",
                new Regex(@"new\s+Sql(Command|Query)\s*\(\s*\$""|\$""\s*(SELECT|UPDATE|INSERT|DELETE)\b", Options),
                "A SQL statement is built by interpolating request data into the string, which allows SQL injection.",
                "Pass the values as parameters (SqlParameter, Dapper or EF Core parameters) instead of interpolating them.",
                "high", 0.9),
            new("Card number written to the logs",
                new Regex(@"Log\w*\s*\(.*\b(Card(Number)?|Pan|Cvv)\b", Options),
                "A log statement includes card data, which puts it in log storage that is not built to hold it (PCI DSS).",
                "Remove the card number from the log message; log the order id or a masked last four digits at most.",
                "high", 0.85),
            new("Secret committed to the repository",
                new Regex(@"sk_(live|test)_[A-Za-z0-9_-]+|""\w*(Secret|Password|ApiKey)\w*""\s*:\s*""[^""]+""", Options),
                "A credential is committed in code or configuration, so everyone with read access to the repository has it.",
                "Revoke the key, remove it from the file and load it from a secret store or user secrets instead.",
                "high", 0.9),
            new("Certificate validation turned off",
                new Regex(@"ServerCertificateCustomValidationCallback|DangerousAcceptAnyServerCertificateValidator", Options),
                "TLS certificate validation is bypassed, which allows man-in-the-middle attacks.",
                "Remove the override and fix the certificate trust instead.",
                "medium", 0.8),
            new("Pull request text tries to instruct reviewers",
                new Regex(@"ignore (all |any )?(previous|prior) instructions|skip (the )?security|pre-?approved|approve this (pr|pull request)|note to (ai|llm) reviewers?", Options),
                "Text in the pull request addresses automated reviewers and asks them to skip checks or approve. That is a prompt-injection attempt and was ignored.",
                "Remove the text from the pull request and review the change normally. Treat the author's other claims with care.",
                "high", 0.8, Scope.WholeInput),
        ]),
        ["design-reviewer"] = ("design",
        [
            new("HttpClient created per call",
                new Regex(@"new\s+HttpClient\s*\(", Options),
                "A new HttpClient is created inside the request path, which exhausts sockets under load and ignores DNS changes.",
                "Inject IHttpClientFactory or a typed client registered with AddHttpClient.",
                "medium", 0.8),
            new("Data access inside a controller",
                new Regex(@"new\s+Sql(Connection|Command)\s*\(|\.ExecuteScalarAsync\(|\.ExecuteNonQueryAsync\(", Options),
                "The controller opens connections and runs SQL itself, mixing HTTP handling with persistence.",
                "Move the queries into a repository or the existing application service and inject it.",
                "medium", 0.75, FileFilter: new Regex(@"Controller", Options)),
            new("Dependency created with new instead of injected",
                new Regex(@"new\s+(?!HttpClient\b)\w+(Service|Repository|Gateway|Client)\s*\(", Options), // HttpClient has its own rule
                "A collaborator is constructed directly, which hard-wires it and makes the code hard to test.",
                "Register it in dependency injection and take it through the constructor.",
                "low", 0.6),
        ]),
        ["extensibility-reviewer"] = ("extensibility",
        [
            new("Switch on provider must change for every new variant",
                new Regex(@"switch\s*\(\s*[\w.]*(Provider|Kind|Type)\s*\)", Options),
                "Behaviour is chosen by switching on a provider or kind, so each new variant means editing this method.",
                "Put each variant behind a common interface and pick the implementation by key (a dictionary of handlers or keyed services).",
                "medium", 0.7),
            new("Hard-coded service URL",
                new Regex(@"""https?://(?!localhost)[^""]+""", Options),
                "An external service address is written into the code, so changing environment or provider needs a code change.",
                "Move the address into configuration (an options class) per environment.",
                "low", 0.65),
        ]),
    };

    public static bool Handles(string agentName) => s_reviewers.ContainsKey(agentName);

    /// <summary>
    /// Reviews <paramref name="input"/> (the rendered user message) as <paramref name="agentName"/>. Returns the
    /// findings object and a short "reasoning" note saying what was scanned, which the ledger stores like a real
    /// model's reasoning.
    /// </summary>
    public static JsonObject Review(string agentName, string input, out string reasoning)
    {
        (string lens, Rule[] rules) = s_reviewers[agentName];
        List<(string File, string Line)> added = AddedLines(input);

        var findings = new List<(Rule Rule, string? File, string Evidence)>();
        foreach (Rule rule in rules)
        {
            if (rule.Scope == Scope.WholeInput)
            {
                Match m = rule.Pattern.Match(input);
                if (m.Success)
                {
                    findings.Add((rule, null, LineAround(input, m.Index)));
                }

                continue;
            }

            // First matching added line wins: one finding per rule keeps the list short, as the reviewer rules ask.
            (string File, string Line) hit = added.FirstOrDefault(a =>
                (rule.FileFilter is null || rule.FileFilter.IsMatch(a.File)) && rule.Pattern.IsMatch(a.Line));
            if (hit.Line is not null)
            {
                findings.Add((rule, hit.File, hit.Line));
            }
        }

        // Most important first, at most five: the same limits the reviewer-rules skill gives a real model.
        var ordered = findings
            .OrderByDescending(f => SeverityRank(f.Rule.Severity))
            .ThenByDescending(f => f.Rule.Confidence)
            .Take(5)
            .ToList();

        var items = new JsonArray();
        foreach ((Rule rule, string? file, string evidence) in ordered)
        {
            items.Add(new JsonObject
            {
                ["title"] = rule.Title,
                ["detail"] = $"{rule.Detail} Seen in {file ?? "the pull request text"}: `{Shorten(evidence)}`",
                ["recommendation"] = rule.Recommendation,
                ["severity"] = rule.Severity,
                ["filePath"] = file,
                ["confidence"] = rule.Confidence,
            });
        }

        string summary = ordered.Count == 0
            ? $"No material {lens} issues stand out in this change. (Fake endpoint: pattern rules, not a model.)"
            : $"{ordered.Count} {lens} issue(s); the most serious: {ordered[0].Rule.Title.ToLowerInvariant()}. (Fake endpoint: pattern rules, not a model.)";

        reasoning = $"Fake reasoning: scanned {added.Count} added line(s) in {added.Select(a => a.File).Distinct().Count()} file(s) " +
            $"against {rules.Length} {lens} rule(s). Matched: {(ordered.Count == 0 ? "none" : string.Join(", ", ordered.Select(f => f.Rule.Title)))}.";

        return new JsonObject { ["summary"] = summary, ["items"] = items };
    }

    // Walks a unified diff: "+++ b/path" names the file, lines starting with a single "+" are additions.
    private static List<(string File, string Line)> AddedLines(string input)
    {
        var lines = new List<(string, string)>();
        string file = "(unknown file)";
        foreach (string line in input.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                file = line[4..].Trim() is var path && path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
            }
            else if (line.StartsWith('+'))
            {
                lines.Add((file, line[1..].Trim()));
            }
        }

        return lines;
    }

    private static string LineAround(string text, int index)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        int end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..117] + "...";

    private static int SeverityRank(string severity) => severity switch { "high" => 3, "medium" => 2, "low" => 1, _ => 0 };
}
