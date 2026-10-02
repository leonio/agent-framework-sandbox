using System.Net;
using System.Text;

namespace CliDesignPipeline.Tools.Integrations;

/// <summary>
/// An in-process stand-in for Jira and Confluence that answers the handful of routes the
/// clients use, with realistic payloads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why mock at the HTTP layer?</b> Everything above it - the typed client, URL building,
/// JSON shaping, error handling, the AIFunction wrapper, the model's tool call - runs exactly as
/// it would against Atlassian. Mocking the <c>JiraClient</c> instead would skip the code most
/// likely to be wrong. Set <c>Integrations:Jira:UseMock=false</c> plus a token to hit the real API.
/// </para>
/// <para>
/// Alternatives: WireMock.Net for richer stubbing (and recording real traffic to replay), or a
/// Jira Cloud free-tier sandbox for true end-to-end tests.
/// </para>
/// </remarks>
public sealed class FakeAtlassianHandler : HttpMessageHandler
{
    private int _pageVersion = 7;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _ = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return (request.Method.Method, path) switch
        {
            ("POST", var p) when p.EndsWith("/comment", StringComparison.Ordinal) =>
                Json(HttpStatusCode.Created, """{ "id": "10042", "self": "https://your-org.atlassian.net/rest/api/3/issue/DEMO-123/comment/10042" }"""),

            ("GET", var p) when p.EndsWith("/transitions", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, """{ "transitions": [ { "id": "11", "name": "To Do" }, { "id": "21", "name": "In Progress" }, { "id": "31", "name": "In Review" }, { "id": "41", "name": "Done" } ] }"""),

            ("POST", var p) when p.EndsWith("/transitions", StringComparison.Ordinal) =>
                new HttpResponseMessage(HttpStatusCode.NoContent),

            ("GET", var p) when p.Contains("/wiki/api/v2/pages/", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, $$"""{ "id": "123456", "title": "Team Apps - Release Notes", "version": { "number": {{_pageVersion}} }, "body": { "storage": { "value": "<p>Existing content.</p>" } } }"""),

            ("PUT", var p) when p.Contains("/wiki/api/v2/pages/", StringComparison.Ordinal) =>
                Json(HttpStatusCode.OK, $$"""{ "id": "123456", "version": { "number": {{++_pageVersion}} } }"""),

            _ => Json(HttpStatusCode.NotFound, $$"""{ "errorMessages": [ "Fake Atlassian has no route for {{request.Method}} {{path}}" ] }"""),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
