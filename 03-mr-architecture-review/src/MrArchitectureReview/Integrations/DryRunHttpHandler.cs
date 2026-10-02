using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;

namespace MrArchitectureReview.Integrations;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that, in dry-run mode, prints each outgoing request and answers it
/// with a canned success instead of sending it.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY mock at the HTTP layer instead of mocking <c>JiraClient</c>?</b> Because then the Jira and
/// Confluence clients are the real code: real URLs, real JSON bodies, real headers. Dry-run output is
/// literally the request you would send, so you can copy it into curl/Postman to try it against a
/// sandbox. Turning dry-run off (<c>Review:Integrations:DryRun=false</c>) changes nothing else.
/// </para>
/// <para>
/// It is plugged in with <c>AddHttpClient&lt;JiraClient&gt;().AddHttpMessageHandler&lt;DryRunHttpHandler&gt;()</c>,
/// the same extension point you would use for Polly resilience, auth token refresh or request logging.
/// Alternative: WireMock.Net or a local Atlassian sandbox. Better fidelity, more setup.
/// </para>
/// </remarks>
public sealed class DryRunHttpHandler(IOptions<ReviewOptions> options, ILogger<DryRunHttpHandler> logger) : DelegatingHandler
{
    private static int s_counter = 100;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!options.Value.Integrations.DryRun)
            return await base.SendAsync(request, cancellationToken);

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("[dry-run] {Method} {Url}\n{Body}", request.Method, request.RequestUri, body);

        var id = Interlocked.Increment(ref s_counter);
        var canned = request.RequestUri?.AbsolutePath switch
        {
            var p when p?.Contains("/rest/api/3/issue", StringComparison.Ordinal) == true =>
                $$"""{"id":"{{id}}","key":"{{options.Value.Integrations.JiraProjectKey}}-{{id}}","self":"dry-run"}""",
            var p when p?.Contains("/wiki/api/v2/", StringComparison.Ordinal) == true =>
                $$"""{"id":"{{id}}","status":"current"}""",
            _ => "{}",
        };

        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(canned, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }
}
