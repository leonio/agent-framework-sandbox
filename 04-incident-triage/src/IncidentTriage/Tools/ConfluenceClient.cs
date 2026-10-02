using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using IncidentTriage.Configuration;

namespace IncidentTriage.Tools;

/// <summary>
/// Confluence Cloud REST v2 client for publishing postmortems. Dry-run (mocked) by default.
/// </summary>
/// <remarks>
/// <para>Like <see cref="JiraClient"/>'s write methods, this is called by the publish executor after human
/// approval, never handed to a model as a tool.</para>
/// <para><b>Format:</b> Confluence stores pages as "storage format" (XHTML). The postmortem agent writes
/// markdown because models write markdown well and humans review it easily; conversion happens here.
/// The conversion below is deliberately tiny (headings, bullets, paragraphs). For real use, render with
/// Markdig (<c>Markdown.ToHtml</c>) and post-process tables/code blocks into Confluence macros.</para>
/// <para><b>Where RAG closes the loop:</b> once a postmortem is published it becomes knowledge. A nightly job
/// that pulls the "Postmortems" space (GET /wiki/api/v2/spaces/{id}/pages) into the <c>knowledge</c> vector
/// collection means the next incident's root-cause agent can cite this one. This sample reads past
/// postmortems from Knowledge/postmortems/ to stand in for that sync.</para>
/// <para>API docs: https://developer.atlassian.com/cloud/confluence/rest/v2/api-group-page/#api-pages-post</para>
/// </remarks>
public sealed class ConfluenceClient(ConfluenceOptions options, HttpClient http)
{
    /// <summary>POST /wiki/api/v2/pages. Returns the page URL.</summary>
    public async Task<string> CreatePageAsync(string title, string markdown, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            // v2 wants a numeric spaceId; resolve it once from the key with GET /wiki/api/v2/spaces?keys=ENG.
            ["spaceId"] = $"<id of space {options.SpaceKey}>",
            ["status"] = "draft", // drafts, not current: a human still owns hitting Publish in Confluence
            ["title"] = title,
            ["parentId"] = options.ParentPageId,
            ["body"] = new JsonObject { ["representation"] = "storage", ["value"] = ToStorageFormat(markdown) },
        };

        if (options.DryRun)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"    [confluence dry-run] POST {options.BaseUrl.TrimEnd('/')}/api/v2/pages  title=\"{title}\" ({markdown.Length} chars)");
            Console.ResetColor();
            return $"{options.BaseUrl.TrimEnd('/')}/spaces/{options.SpaceKey}/pages/draft-{Guid.NewGuid():N}"[..^24];
        }

        using var response = await http.PostAsJsonAsync("api/v2/pages", body, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return $"{options.BaseUrl.TrimEnd('/')}{created?["_links"]?["webui"]}";
    }

    public static HttpClient CreateHttpClient(ConfluenceOptions options) =>
        // Same Basic auth (email + API token) as Jira: they share Atlassian identity.
        new() { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/") };

    private static string ToStorageFormat(string markdown)
    {
        var sb = new StringBuilder();
        var inList = false;
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = WebUtility.HtmlEncode(raw.TrimEnd());
            var isBullet = line.StartsWith("- ", StringComparison.Ordinal);
            if (inList && !isBullet) { sb.Append("</ul>"); inList = false; }

            sb.Append(line switch
            {
                _ when line.StartsWith("### ", StringComparison.Ordinal) => $"<h3>{line[4..]}</h3>",
                _ when line.StartsWith("## ", StringComparison.Ordinal) => $"<h2>{line[3..]}</h2>",
                _ when line.StartsWith("# ", StringComparison.Ordinal) => $"<h1>{line[2..]}</h1>",
                _ when isBullet => (inList ? "" : "<ul>") + $"<li>{line[2..]}</li>",
                "" => "",
                _ => $"<p>{line}</p>",
            });
            inList |= isBullet;
        }
        if (inList) sb.Append("</ul>");
        return sb.ToString();
    }
}
