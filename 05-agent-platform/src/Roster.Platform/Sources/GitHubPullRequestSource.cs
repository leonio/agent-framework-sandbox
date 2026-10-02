using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Roster.Platform.Sources;

public sealed class GitHubOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>
    /// Optional token (configuration <c>GitHub:Token</c>) for private repositories and a higher rate limit. Public
    /// pull requests work without one. Slice 2 can use the person's own <c>github-token</c> credential instead.
    /// </summary>
    public string? Token { get; set; }
}

/// <summary>
/// Fetches a public (or, with a token, private) GitHub pull request from a URL such as
/// <c>https://github.com/contoso/shop-api/pull/42</c>: metadata, changed files and the unified diff.
/// </summary>
/// <remarks>
/// Plain REST rather than an SDK or a tool the model calls: fetching is the same three requests every time, so it is
/// code, not judgement (the same reasoning as sample 03's source). Unlike sample 03 it does not clone the repository:
/// slice 1's reviewers have no file tools, so the diff is all they read.
/// </remarks>
public sealed partial class GitHubPullRequestSource(HttpClient http, IOptions<GitHubOptions> options, ILogger<GitHubPullRequestSource> logger)
    : IPullRequestSource
{
    public bool CanHandle(string source) => PullUrl().IsMatch(source);

    public async Task<PullRequestSnapshot> FetchAsync(string source, CancellationToken cancellationToken)
    {
        Match url = PullUrl().Match(source);
        if (!url.Success)
        {
            throw new ArgumentException($"Not a GitHub pull request URL: {source}", nameof(source));
        }

        string owner = url.Groups["owner"].Value, repo = url.Groups["repo"].Value, number = url.Groups["number"].Value;
        string path = $"repos/{owner}/{repo}/pulls/{number}";

        // 1. Metadata. 2. Changed files (first page of 100 is plenty for a surface-level review). 3. The diff: the same
        // endpoint as 1, asked for a different media type.
        GitHubPull pull = await GetJsonAsync<GitHubPull>(path, cancellationToken);
        List<GitHubFile> files = await GetJsonAsync<List<GitHubFile>>($"{path}/files?per_page=100", cancellationToken);
        if (files.Count == 100)
        {
            logger.LogWarning("{Pull} has 100 or more files; the reviewers see the first 100 names", source);
        }

        string diff = await GetStringAsync(path, "application/vnd.github.diff", cancellationToken);

        return new PullRequestSnapshot(
            Reference: $"{owner}/{repo}#{number}",
            Title: pull.Title,
            Description: pull.Body ?? "",
            Author: pull.User.Login,
            Diff: diff,
            Files: [.. files.Select(f => f.Filename)]);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(path, "application/vnd.github+json");
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException($"GitHub returned an empty response for {path}.");
    }

    private async Task<string> GetStringAsync(string path, string accept, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(path, accept);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private HttpRequestMessage CreateRequest(string path, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.Value.ApiBaseUrl), path));
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd("roster");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(options.Value.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.Token);
        }

        return request;
    }

    // https://github.com/{owner}/{repo}/pull/{number}, with or without a trailing path such as /files.
    [GeneratedRegex(@"^https://github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+)/pull/(?<number>\d+)(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PullUrl();

    // Only the fields we read from GitHub's responses.
    private sealed record GitHubPull(string Title, string? Body, GitHubUser User);

    private sealed record GitHubUser(string Login);

    private sealed record GitHubFile(string Filename);
}
