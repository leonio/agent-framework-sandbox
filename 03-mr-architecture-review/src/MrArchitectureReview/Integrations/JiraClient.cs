using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;

namespace MrArchitectureReview.Integrations;

/// <summary>
/// Minimal Jira Cloud REST v3 client: create an issue for an accepted finding.
/// </summary>
/// <remarks>
/// Typed <see cref="HttpClient"/> created by <c>IHttpClientFactory</c> (registered in Hosting/ServiceRegistration.cs). That is
/// the guideline the sample PR itself violates, so the reviewer agents have something to say about it!
/// Jira v3 wants descriptions in Atlassian Document Format (ADF); we build the simplest valid document.
/// Docs: https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/#api-rest-api-3-issue-post
/// Alternative: Atlassian's official MCP server (Rovo), which exposes Jira/Confluence as MCP tools.
/// That is attractive once you need many operations, see <c>GitHubMcpTools.cs</c> for the MCP wiring.
/// </remarks>
public sealed class JiraClient(HttpClient http, IOptions<ReviewOptions> options)
{
    private readonly IntegrationOptions _o = options.Value.Integrations;

    public async Task<string> CreateIssueAsync(string summary, string description, string priority, CancellationToken ct = default)
    {
        var payload = new
        {
            fields = new
            {
                project = new { key = _o.JiraProjectKey },
                issuetype = new { name = "Task" },
                summary,
                priority = new { name = priority },
                labels = new[] { "architecture-review", "ai-assisted" },
                description = new
                {
                    type = "doc",
                    version = 1,
                    content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = description } } } },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_o.JiraBaseUrl), "rest/api/3/issue"))
        {
            Content = JsonContent.Create(payload),
        };
        AtlassianAuth.Apply(request, _o);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedIssue>(ct);
        return created?.Key ?? "UNKNOWN";
    }

    private sealed record CreatedIssue(string Id, string Key);
}

/// <summary>Minimal Confluence Cloud REST v2 client: add a decision-log entry to a page.</summary>
/// <remarks>
/// We post a <i>footer comment</i> on a "decision log" page: one request, append-only, no edit conflicts.
/// Editing the page body itself needs GET (to read the current version number) then PUT with
/// version+1, and a retry when someone else edited in between. Worth it for a curated page, overkill here.
/// Docs: https://developer.atlassian.com/cloud/confluence/rest/v2/api-group-comment/#api-footer-comments-post
/// </remarks>
public sealed class ConfluenceClient(HttpClient http, IOptions<ReviewOptions> options)
{
    private readonly IntegrationOptions _o = options.Value.Integrations;

    public async Task<string> AppendDecisionAsync(string title, string text, CancellationToken ct = default)
    {
        var html = $"<h3>{System.Net.WebUtility.HtmlEncode(title)}</h3><p>{System.Net.WebUtility.HtmlEncode(text)}</p>";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_o.ConfluenceBaseUrl), "api/v2/footer-comments"))
        {
            Content = JsonContent.Create(new
            {
                pageId = _o.ConfluenceDecisionLogPageId,
                body = new { representation = "storage", value = html },
            }),
        };
        AtlassianAuth.Apply(request, _o);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(ct))?.Id ?? "UNKNOWN";
    }

    private sealed record Created(string Id);
}

internal static class AtlassianAuth
{
    public static void Apply(HttpRequestMessage request, IntegrationOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.AtlassianEmail) || string.IsNullOrWhiteSpace(o.AtlassianApiToken))
            return; // dry-run doesn't need credentials
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.AtlassianEmail}:{o.AtlassianApiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }
}
