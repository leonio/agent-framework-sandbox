using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Sources;

/// <summary>
/// Fetches a real GitHub pull request: metadata and diff from the REST API, and a shallow clone of the
/// PR head so the reviewer agents' tools can read files that are not in the diff.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY plain REST + git rather than an SDK or the GitHub MCP server?</b> This step is deterministic
/// plumbing: the same three calls every run, no reasoning required. Putting it behind an LLM tool
/// call (e.g. letting an agent call the GitHub MCP server's <c>get_pull_request</c>) would add latency,
/// cost and a chance of the model fetching the wrong thing. Rule of thumb used throughout this sample:
/// <i>use code for what is known in advance, use agents for judgement</i>. (MCP is shown where it
/// earns its place, in <c>Integrations/GitHubMcpTools.cs</c>.)
/// Alternative: Octokit.NET. Typed and convenient, but another dependency for three GET requests.
/// </para>
/// <para>
/// <b>WHY clone at all, when the API returns the diff?</b> A diff shows what changed, not what it
/// changed <i>against</i>. "Does the codebase already have an IPaymentGateway?" can only be answered by
/// reading the rest of the repo. A depth-1 fetch of <c>pull/N/head</c> keeps the clone cheap.
/// </para>
/// <para>
/// <b>Security:</b> the clone is untrusted content. We never build or execute it, git runs without a
/// shell (arguments are passed as a list, so a malicious branch name cannot inject commands), and the
/// file tools are confined to the clone directory (see <c>Tools/RepositoryTools.cs</c>).
/// </para>
/// </remarks>
public sealed class GitHubPullRequestSource(
    HttpClient http,
    IOptions<ReviewOptions> options,
    ILogger<GitHubPullRequestSource> logger) : IPullRequestSource
{
    private readonly ReviewOptions _options = options.Value;

    public bool CanHandle(ReviewRequest request) => PullRequestUrl.TryParse(request.Source, out _);

    public async Task<PullRequestSnapshot> FetchAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        if (!PullRequestUrl.TryParse(request.Source, out var parsed))
            throw new ArgumentException($"Not a GitHub PR URL: {request.Source}", nameof(request));
        var url = parsed.Value;

        var apiPath = $"repos/{url.Owner}/{url.Repository}/pulls/{url.Number}";

        // 1. Metadata
        var pr = await SendAsync<GitHubPull>(apiPath, "application/vnd.github+json", cancellationToken);

        // 2. Changed files. The API pages at 100; a "surface level" review of a 100+ file PR is not
        //    meaningful anyway, so we take the first page and say so in the logs.
        var files = await SendAsync<List<GitHubFile>>($"{apiPath}/files?per_page=100", "application/vnd.github+json", cancellationToken);
        if (files.Count == 100)
            logger.LogWarning("PR has 100+ files; only the first 100 are listed to the reviewers");

        // 3. Unified diff (same endpoint, different media type)
        var diff = await SendStringAsync(apiPath, "application/vnd.github.diff", cancellationToken);

        // 4. Shallow checkout of the PR head
        var checkout = await CloneHeadAsync(url, cancellationToken);

        return new PullRequestSnapshot(
            url.Owner, url.Repository, url.Number,
            pr.Title, pr.Body ?? "", pr.User.Login, pr.Base.Ref, pr.Head.Ref,
            [.. files.Select(f => new ChangedFile(f.Filename, f.Status, f.Additions, f.Deletions))],
            diff, checkout);
    }

    private async Task<T> SendAsync<T>(string path, string accept, CancellationToken ct)
    {
        using var req = CreateRequest(path, accept);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidDataException($"Empty response from {path}");
    }

    private async Task<string> SendStringAsync(string path, string accept, CancellationToken ct)
    {
        using var req = CreateRequest(path, accept);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private HttpRequestMessage CreateRequest(string path, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(_options.GitHub.ApiBaseUrl), path));
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(_options.GitHub.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GitHub.Token);
        return req;
    }

    private async Task<string> CloneHeadAsync(PullRequestUrl url, CancellationToken ct)
    {
        var target = Path.GetFullPath(Path.Combine(_options.Storage.WorkDirectory,
            $"{url.Owner}-{url.Repository}-pr{url.Number}-{DateTime.UtcNow:yyyyMMddHHmmss}"));
        Directory.CreateDirectory(target);

        // init + fetch pull/N/head works for PRs from forks too, without needing the fork's URL.
        await GitAsync(target, ct, "init", "--quiet");
        await GitAsync(target, ct, "remote", "add", "origin", url.CloneUrl);
        await GitAsync(target, ct, "fetch", "--quiet", "--depth", "1", "origin", $"pull/{url.Number}/head");
        await GitAsync(target, ct, "checkout", "--quiet", "FETCH_HEAD");

        logger.LogInformation("Cloned {Pr} head into {Path}", url, target);
        return target;
    }

    private static async Task GitAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false, // no shell: arguments are never re-parsed
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Never let a cloned repo's hooks or config run anything.
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {await stderr}");
    }

    // Minimal shapes of the GitHub REST responses: only the fields we use.
    private sealed record GitHubPull(string Title, string? Body, GitHubUser User, GitHubRef Base, GitHubRef Head);
    private sealed record GitHubUser(string Login);
    private sealed record GitHubRef(string Ref);
    private sealed record GitHubFile(
        string Filename, string Status, int Additions, int Deletions,
        [property: JsonPropertyName("patch")] string? Patch);
}
