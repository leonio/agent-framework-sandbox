using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentTriage.Ai;
using IncidentTriage.Configuration;
using IncidentTriage.Workflow;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Tools;

/// <summary>
/// Jira Cloud REST v3 client. Dry-run (mocked) by default: it prints the request it would send.
/// </summary>
/// <remarks>
/// <para><b>Two ways the workflow touches Jira, on purpose:</b></para>
/// <list type="bullet">
///   <item><b>Read</b> (<c>search_jira_issues</c>) is exposed to the jira-drafter agent as a tool. Reads
///     are safe to let a model call as often as it likes.</item>
///   <item><b>Write</b> (<see cref="CreateIssueAsync"/>, <see cref="AddCommentAsync"/>) is NOT a tool. The
///     publish executor calls it with plain C# after a human approved the drafts. Side effects that other
///     people see should sit behind a deterministic step and a human gate, not inside a model's loop.
///     If you do want the model to write, wrap the function in <c>ApprovalRequiredAIFunction</c> so the
///     agent pauses with a <c>ToolApprovalRequestContent</c> until someone approves.</item>
/// </list>
/// <para><b>Alternatives:</b> the Atlassian Remote MCP server (https://www.atlassian.com/platform/remote-mcp-server)
/// exposes Jira + Confluence as MCP tools with OAuth, which removes this class entirely at the cost of less
/// control over what the model can do. See <see cref="McpObservabilityTools"/> for how MCP tools plug in.</para>
/// <para>API docs: https://developer.atlassian.com/cloud/jira/platform/rest/v3/</para>
/// </remarks>
public sealed class JiraClient(JiraOptions options, HttpClient http)
{
    // A tiny fake backlog so search returns something plausible offline.
    private static readonly (string Key, string Summary, string Status)[] FakeBacklog =
    [
        ("OPS-1412", "checkout-api: intermittent DB connection timeouts during flash sale", "Done"),
        ("OPS-1533", "checkout-api: raise Max Pool Size after load test", "Won't Do"),
        ("OPS-1601", "search-api: Redis cache stampede on catalogue reindex", "Done"),
        ("OPS-1650", "payments-gateway: retries amplify provider brownouts", "In Progress"),
    ];

    private int _nextKey = 1700;

    public IEnumerable<AITool> AsReadOnlyAITools() => [AIFunctionFactory.Create(SearchIssuesAsync, "search_jira_issues")];

    [Description("Search existing Jira issues by free text. Use it to find duplicates or related incidents before drafting a new ticket.")]
    public async Task<string> SearchIssuesAsync([Description("Free text, e.g. a service name plus a symptom.")] string text, CancellationToken ct = default)
    {
        // JQL injection is a thing: never paste model output into JQL unescaped.
        var jql = $"project = {options.ProjectKey} AND text ~ \"{text.Replace("\"", "\\\"")}\" ORDER BY updated DESC";

        if (options.DryRun)
        {
            Log("GET", $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&maxResults=5&fields=summary,status");
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = FakeBacklog.Where(i => words.Any(w => w.Length > 3 && i.Summary.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            return hits.Count == 0 ? "No matching issues." : string.Join('\n', hits.Select(h => $"{h.Key} [{h.Status}] {h.Summary}"));
        }

        var result = await http.GetFromJsonAsync<JsonObject>($"rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&maxResults=5&fields=summary,status", ct);
        return string.Join('\n', result?["issues"]?.AsArray().Select(i => $"{i?["key"]} [{i?["fields"]?["status"]?["name"]}] {i?["fields"]?["summary"]}") ?? []);
    }

    /// <summary>POST /rest/api/3/issue. Returns the new issue key.</summary>
    public async Task<string> CreateIssueAsync(JiraIssueDraft draft, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = options.ProjectKey },
                ["summary"] = draft.Summary,
                ["issuetype"] = new JsonObject { ["name"] = draft.IssueType },
                ["priority"] = new JsonObject { ["name"] = draft.Priority.ToString() },
                ["labels"] = new JsonArray([.. draft.Labels.Select(l => JsonValue.Create(l.Replace(' ', '-')))]),
                // v3 requires Atlassian Document Format (ADF), not plain text or wiki markup.
                ["description"] = ToAdf(draft.Description),
            },
        };

        if (options.DryRun)
        {
            Log("POST", "/rest/api/3/issue", body);
            return $"{options.ProjectKey}-{Interlocked.Increment(ref _nextKey)}";
        }

        using var response = await http.PostAsJsonAsync("rest/api/3/issue", body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["key"]?.GetValue<string>() ?? "?";
    }

    /// <summary>POST /rest/api/3/issue/{key}/comment. Used to "update the ticket with the work done".</summary>
    public async Task AddCommentAsync(string issueKey, string text, CancellationToken ct = default)
    {
        var body = new JsonObject { ["body"] = ToAdf(text) };
        if (options.DryRun)
        {
            Log("POST", $"/rest/api/3/issue/{issueKey}/comment", body);
            return;
        }
        using var response = await http.PostAsJsonAsync($"rest/api/3/issue/{issueKey}/comment", body, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Configures auth for real calls: Jira Cloud uses Basic auth with email + API token.</summary>
    public static HttpClient CreateHttpClient(JiraOptions options)
    {
        var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };
        var email = Environment.GetEnvironmentVariable("JIRA_EMAIL");
        var token = Environment.GetEnvironmentVariable("JIRA_API_TOKEN");
        if (email is not null && token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
        return http;
    }

    /// <summary>Minimal plain-text to ADF: one paragraph per line. Enough for tickets; use a real converter for rich text.</summary>
    private static JsonObject ToAdf(string text) => new()
    {
        ["type"] = "doc",
        ["version"] = 1,
        ["content"] = new JsonArray([.. text.Split('\n').Select(line => (JsonNode)new JsonObject
        {
            ["type"] = "paragraph",
            ["content"] = string.IsNullOrWhiteSpace(line) ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line }),
        })]),
    };

    private void Log(string verb, string path, JsonNode? body = null)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    [jira dry-run] {verb} {options.BaseUrl.TrimEnd('/')}{path}");
        if (body is not null)
        {
            var json = body.ToJsonString(JsonDefaults.Compact);
            Console.WriteLine($"    [jira dry-run] body: {(json.Length > 160 ? json[..160] + "…" : json)}");
        }
        Console.ResetColor();
    }
}
