using System.Text.Json;

namespace Roster.Platform.Sources;

/// <summary>
/// A pull request as the review scenario needs it: what it is, what it says, and what it changes. Everything except
/// <see cref="Reference"/> and <see cref="Files"/> was written by someone else and reaches the reviewers fenced as
/// untrusted.
/// </summary>
public sealed record PullRequestSnapshot(
    string Reference,
    string Title,
    string Description,
    string Author,
    string Diff,
    IReadOnlyList<string> Files);

/// <summary>Fetches a pull request from wherever <c>Assignment.Source</c> points.</summary>
public interface IPullRequestSource
{
    bool CanHandle(string source);

    Task<PullRequestSnapshot> FetchAsync(string source, CancellationToken cancellationToken);
}

/// <summary>Picks the first source that understands an assignment's <c>Source</c> string.</summary>
public sealed class PullRequestSources(IEnumerable<IPullRequestSource> sources)
{
    /// <summary>
    /// Diffs longer than this are cut, with a note saying so. A surface-level review of a huge change is not
    /// meaningful, and it would spend a person's tokens on noise.
    /// </summary>
    public const int MaxDiffCharacters = 60_000;

    public async Task<PullRequestSnapshot> FetchAsync(string source, CancellationToken cancellationToken)
    {
        IPullRequestSource handler = sources.FirstOrDefault(s => s.CanHandle(source))
            ?? throw new NotSupportedException($"No pull request source understands '{source}'. Use fixture:sample-pr or a GitHub pull request URL.");

        PullRequestSnapshot snapshot = await handler.FetchAsync(source, cancellationToken);
        return snapshot.Diff.Length <= MaxDiffCharacters
            ? snapshot
            : snapshot with
            {
                Diff = snapshot.Diff[..MaxDiffCharacters] +
                    $"\n\n[Diff cut at {MaxDiffCharacters:N0} characters of {snapshot.Diff.Length:N0}. Review what is shown.]",
            };
    }
}

/// <summary>
/// The sample pull request bundled with the platform (<c>fixture:sample-pr</c>), from sample 03: a payments endpoint
/// with deliberate problems, including description text that tries to talk the reviewers into approving it.
/// </summary>
public sealed class FixturePullRequestSource : IPullRequestSource
{
    public const string SamplePr = "fixture:sample-pr";

    private static readonly string s_root = Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-pr");

    public bool CanHandle(string source) => source.Equals(SamplePr, StringComparison.OrdinalIgnoreCase);

    public async Task<PullRequestSnapshot> FetchAsync(string source, CancellationToken cancellationToken)
    {
        using JsonDocument pr = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(s_root, "pull-request.json"), cancellationToken));
        JsonElement root = pr.RootElement;
        string diff = await File.ReadAllTextAsync(Path.Combine(s_root, "changes.diff"), cancellationToken);

        return new PullRequestSnapshot(
            Reference: $"{root.GetProperty("owner").GetString()}/{root.GetProperty("repository").GetString()}#{root.GetProperty("number").GetInt32()}",
            Title: root.GetProperty("title").GetString() ?? "",
            Description: root.GetProperty("description").GetString() ?? "",
            Author: root.GetProperty("author").GetString() ?? "",
            Diff: diff,
            Files: [.. root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)]);
    }
}
