using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SdlcStudio.Api.Options;

namespace SdlcStudio.Api.Tools;

// ---------------------------------------------------------------------------------------------
// "Tooling" = plain API calls the studio makes to other systems: Jira for work tracking and
// Confluence for documentation. Both run in Mock mode by default (they log what they *would* do
// and return fake ids) and switch to the real REST APIs with Studio:Integrations:Mode = Live.
//
// TWO WAYS TO USE AN API FROM AN AGENTIC APP, both shown in this sample:
//  1. Deterministic calls from workflow code (PlanPublisher creates Jira issues, TestingPublisher
//     publishes a Confluence page). Use this when the side effect must ALWAYS happen, exactly once.
//  2. Exposed as a tool the model may call (JiraTools.update_jira_ticket, given to the developer
//     agent). Use this when the model should decide whether and what to write.
// Rule of thumb: keep irreversible or compliance-relevant side effects in code (1); give the model
// tools (2) for optional, low-risk, context-dependent actions.
//
// ALTERNATIVE: an Atlassian MCP server (see GitHubMcpTools.cs for the MCP pattern) gives an agent the
// whole Jira/Confluence API surface without writing these clients, at the cost of less control over
// which operations it can perform.
// ---------------------------------------------------------------------------------------------

/// <summary>Typed HttpClient for the Jira Cloud REST API v3.</summary>
public sealed class JiraClient(HttpClient http, IOptions<StudioOptions> options, ILogger<JiraClient> logger)
{
    private readonly IntegrationOptions _o = options.Value.Integrations;
    private static int _mockCounter = 100;

    public bool IsMock => _o.Mode == IntegrationMode.Mock;

    public async Task<string> CreateIssueAsync(string summary, string description, string issueType, CancellationToken ct = default)
    {
        if (IsMock)
        {
            var key = $"{_o.JiraProjectKey}-{Interlocked.Increment(ref _mockCounter)}";
            logger.LogInformation("[mock jira] create {Type} {Key}: {Summary}", issueType, key, summary);
            return key;
        }

        Authorize();
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = _o.JiraProjectKey },
                ["summary"] = summary,
                ["issuetype"] = new JsonObject { ["name"] = issueType },
                ["description"] = Adf(description),
            },
        };
        using var response = await http.PostAsJsonAsync($"{_o.JiraBaseUrl}/rest/api/3/issue", body, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return created?["key"]?.GetValue<string>() ?? "unknown";
    }

    public async Task AddCommentAsync(string issueKey, string comment, CancellationToken ct = default)
    {
        if (IsMock)
        {
            logger.LogInformation("[mock jira] comment on {Key}: {Comment}", issueKey, comment);
            return;
        }

        Authorize();
        using var response = await http.PostAsJsonAsync(
            $"{_o.JiraBaseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment",
            new JsonObject { ["body"] = Adf(comment) }, ct);
        response.EnsureSuccessStatusCode();
    }

    private void Authorize() => http.DefaultRequestHeaders.Authorization ??= BasicAuth(_o);

    /// <summary>Jira v3 wants Atlassian Document Format; this is the smallest valid document.</summary>
    private static JsonObject Adf(string text) => new()
    {
        ["type"] = "doc",
        ["version"] = 1,
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "paragraph",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }),
    };

    internal static AuthenticationHeaderValue BasicAuth(IntegrationOptions o) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.AtlassianEmail}:{o.AtlassianApiToken}")));
}

/// <summary>Typed HttpClient for the Confluence Cloud REST API.</summary>
public sealed class ConfluenceClient(HttpClient http, IOptions<StudioOptions> options, ILogger<ConfluenceClient> logger)
{
    private readonly IntegrationOptions _o = options.Value.Integrations;

    /// <returns>The URL of the created page.</returns>
    public async Task<string> PublishPageAsync(string title, string markdown, CancellationToken ct = default)
    {
        if (_o.Mode == IntegrationMode.Mock)
        {
            var url = $"{_o.ConfluenceBaseUrl}/spaces/{_o.ConfluenceSpaceKey}/pages/mock-{Guid.NewGuid():N}";
            logger.LogInformation("[mock confluence] publish '{Title}' ({Length} chars) -> {Url}", title, markdown.Length, url);
            return url;
        }

        http.DefaultRequestHeaders.Authorization ??= JiraClient.BasicAuth(_o);
        // Confluence "storage" format is XHTML. We wrap markdown in a code macro to stay dependency free.
        // ALTERNATIVE: convert markdown to HTML with Markdig first for a properly formatted page.
        var storage = $"<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[{markdown}]]></ac:plain-text-body></ac:structured-macro>";
        var body = new JsonObject
        {
            ["type"] = "page",
            ["title"] = title,
            ["space"] = new JsonObject { ["key"] = _o.ConfluenceSpaceKey },
            ["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = storage, ["representation"] = "storage" } },
        };
        using var response = await http.PostAsJsonAsync($"{_o.ConfluenceBaseUrl}/rest/api/content", body, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return $"{_o.ConfluenceBaseUrl}{page?["_links"]?["webui"]?.GetValue<string>()}";
    }
}

/// <summary>
/// Jira exposed as a tool the developer agent can call. The <paramref name="onCall"/> callback lets the
/// workflow record the tool call in the run's activity log, so humans can see what the agent did.
/// <paramref name="issueKeys"/> maps the plan's story keys (ST-1) to real Jira keys (STUDIO-101).
/// </summary>
public sealed class JiraTools(JiraClient jira, IReadOnlyDictionary<string, string> issueKeys, Func<string, Task> onCall)
{
    public IList<AITool> AsTools() => [AIFunctionFactory.Create(UpdateTicketAsync, "update_jira_ticket")];

    [Description("Adds a progress comment to a Jira issue (user story).")]
    public async Task<string> UpdateTicketAsync(
        [Description("Story key, e.g. ST-1")] string issueKey,
        [Description("Short progress update")] string comment)
    {
        var jiraKey = issueKeys.GetValueOrDefault(issueKey, issueKey);
        await jira.AddCommentAsync(jiraKey, comment);
        await onCall($"Tool call update_jira_ticket({issueKey} -> {jiraKey}): {comment}");
        return $"Comment added to {jiraKey}.";
    }
}
