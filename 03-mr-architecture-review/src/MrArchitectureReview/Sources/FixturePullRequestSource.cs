using System.Text.Json;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Sources;

/// <summary>
/// Loads the bundled sample PR from <c>Fixtures/SamplePr</c>: metadata, a unified diff and the files as
/// they look at the PR head (so the repository tools have something real to read).
/// </summary>
/// <remarks>
/// The fixture is a small "add payments endpoint" change with planted issues for each reviewer:
/// SQL built by string interpolation (security), <c>new HttpClient()</c> inside a controller (design),
/// and a <c>switch</c> on payment provider (extensibility). Planted issues make it easy to judge
/// whether a real model is doing its job when you switch the provider away from Mock.
/// </remarks>
public sealed class FixturePullRequestSource(string? fixtureDirectory = null) : IPullRequestSource
{
    private readonly string _root = fixtureDirectory ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "SamplePr");

    public bool CanHandle(ReviewRequest request) =>
        request.Source.Equals("sample", StringComparison.OrdinalIgnoreCase);

    public async Task<PullRequestSnapshot> FetchAsync(ReviewRequest request, CancellationToken cancellationToken)
    {
        await using var meta = File.OpenRead(Path.Combine(_root, "pull-request.json"));
        var info = await JsonSerializer.DeserializeAsync<FixtureMetadata>(meta, JsonSerializerOptions.Web, cancellationToken)
                   ?? throw new InvalidDataException("pull-request.json is empty");

        var diff = await File.ReadAllTextAsync(Path.Combine(_root, "changes.diff"), cancellationToken);

        return new PullRequestSnapshot(
            info.Owner, info.Repository, info.Number, info.Title, info.Description, info.Author,
            info.BaseRef, info.HeadRef, info.Files, diff,
            LocalCheckoutPath: Path.Combine(_root, "repo"));
    }

    private sealed record FixtureMetadata(
        string Owner, string Repository, int Number, string Title, string Description,
        string Author, string BaseRef, string HeadRef, IReadOnlyList<ChangedFile> Files);
}
