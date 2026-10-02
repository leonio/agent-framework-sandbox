using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Sources;

/// <summary>
/// Gets a <see cref="PullRequestSnapshot"/> from somewhere: GitHub, a local fixture, or (by adding an
/// implementation) GitLab merge requests, Azure DevOps PRs or Bitbucket.
/// </summary>
/// <remarks>
/// WHY an interface for something with two implementations? The workflow's first executor depends on
/// this, not on GitHub. That is what lets the whole pipeline run offline and in tests, and it is the
/// natural seam for "MR" sources other than GitHub (the user's original ask said MR, i.e. GitLab
/// terminology, so a GitLab source is the obvious next implementation).
/// </remarks>
public interface IPullRequestSource
{
    /// <summary>True if this source understands the given request (URL shape, keyword...).</summary>
    bool CanHandle(ReviewRequest request);

    Task<PullRequestSnapshot> FetchAsync(ReviewRequest request, CancellationToken cancellationToken);
}

/// <summary>Picks the first source that can handle a request. Registered sources are tried in order.</summary>
public sealed class PullRequestSourceResolver(IEnumerable<IPullRequestSource> sources)
{
    public IPullRequestSource Resolve(ReviewRequest request) =>
        sources.FirstOrDefault(s => s.CanHandle(request))
        ?? throw new NotSupportedException(
            $"Don't know how to fetch '{request.Source}'. Use a https://github.com/<owner>/<repo>/pull/<n> URL or 'sample'.");
}
