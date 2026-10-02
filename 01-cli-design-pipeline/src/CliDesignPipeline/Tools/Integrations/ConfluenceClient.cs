using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CliDesignPipeline.Tools.Integrations;

/// <summary>
/// Minimal Confluence Cloud REST v2 client: append a section to an existing page.
/// </summary>
/// <remarks>
/// Confluence uses optimistic concurrency: an update must carry <c>version.number = current + 1</c>
/// or it is rejected with 409. So "update a page" is always read-modify-write. If two agents (or
/// an agent and a human) edit the same page you will see 409s; the right fix is to re-read and
/// retry once, not to blindly overwrite. Docs:
/// https://developer.atlassian.com/cloud/confluence/rest/v2/api-group-page/#api-pages-id-put
/// </remarks>
public sealed class ConfluenceClient(HttpClient http)
{
    public async Task<string> AppendSectionAsync(string pageId, string heading, string markdownText, CancellationToken ct = default)
    {
        var page = await http.GetFromJsonAsync<JsonObject>($"wiki/api/v2/pages/{pageId}?body-format=storage", ct)
            ?? throw new InvalidOperationException($"Page {pageId} not found.");

        var title = page["title"]!.GetValue<string>();
        var version = page["version"]!["number"]!.GetValue<int>();
        var existing = page["body"]?["storage"]?["value"]?.GetValue<string>() ?? "";

        // Storage format is XHTML. We keep the agent's text as plain paragraphs; converting
        // Markdown properly (Markdig) is a reasonable next step.
        var paragraphs = string.Concat(markdownText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => $"<p>{System.Net.WebUtility.HtmlEncode(line.TrimStart('-', ' '))}</p>"));
        var body = $"{existing}<h2>{System.Net.WebUtility.HtmlEncode(heading)}</h2>{paragraphs}";

        using var response = await http.PutAsJsonAsync($"wiki/api/v2/pages/{pageId}", new
        {
            id = pageId,
            status = "current",
            title,
            body = new { representation = "storage", value = body },
            version = new { number = version + 1, message = "Updated by release agent" },
        }, ct);
        response.EnsureSuccessStatusCode();
        return $"Page '{title}' updated to version {version + 1}.";
    }
}
