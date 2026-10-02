using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CliDesignPipeline.Tools.Integrations;

/// <summary>
/// Minimal Jira Cloud REST v3 client: just the two calls the release agent needs.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>typed HttpClient</b> (registered with <c>services.AddHttpClient&lt;JiraClient&gt;()</c>).
/// IHttpClientFactory owns handler lifetime (no socket exhaustion, DNS refresh), and the base
/// address / auth header are configured once in Program.cs. Swapping in the fake handler for
/// offline runs is one <c>ConfigurePrimaryHttpMessageHandler</c> call; this class never knows.
/// </para>
/// <para>
/// Why hand-written instead of an SDK? Agent tools should be <i>narrow</i>. Two methods with
/// clear names make better tools than a generated client with 600 operations, and it keeps the
/// blast radius of a misbehaving model small. Alternatives: Atlassian's Rovo/remote MCP server
/// (zero code, but you expose whatever tools it ships), or a generated client via Kiota from
/// the OpenAPI spec when you need lots of endpoints.
/// </para>
/// <para>Docs: https://developer.atlassian.com/cloud/jira/platform/rest/v3/</para>
/// </remarks>
public sealed class JiraClient(HttpClient http)
{
    /// <summary>POST /rest/api/3/issue/{key}/comment. Jira v3 wants Atlassian Document Format (ADF), not plain text.</summary>
    public async Task<string> AddCommentAsync(string issueKey, string text, CancellationToken ct = default)
    {
        JsonObject adf = new()
        {
            ["body"] = new JsonObject
            {
                ["type"] = "doc",
                ["version"] = 1,
                ["content"] = new JsonArray(
                    [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonNode? (line) => new JsonObject
                    {
                        ["type"] = "paragraph",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line }),
                    })]),
            },
        };

        using var response = await http.PostAsJsonAsync($"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment", adf, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return $"Comment {created?["id"]} added to {issueKey}.";
    }

    /// <summary>
    /// Transitions are looked up by <i>name</i> (GET .../transitions) because transition ids differ
    /// per workflow and per project; hard-coding "31" is a classic Jira-integration bug.
    /// </summary>
    public async Task<string> TransitionAsync(string issueKey, string transitionName, CancellationToken ct = default)
    {
        var path = $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/transitions";
        var available = await http.GetFromJsonAsync<JsonObject>(path, ct);
        var match = available?["transitions"]?.AsArray()
            .FirstOrDefault(t => string.Equals(t?["name"]?.GetValue<string>(), transitionName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var names = available?["transitions"]?.AsArray().Select(t => t?["name"]?.GetValue<string>());
            return $"ERROR: '{transitionName}' is not available for {issueKey}. Available: {string.Join(", ", names ?? [])}.";
        }

        using var response = await http.PostAsJsonAsync(path, new { transition = new { id = match["id"]!.GetValue<string>() } }, ct);
        response.EnsureSuccessStatusCode();
        return $"{issueKey} moved to '{transitionName}'.";
    }
}
