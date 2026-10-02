using MrArchitectureReview.Domain;
using MrArchitectureReview.Rag;
using MrArchitectureReview.Sources;
using MrArchitectureReview.Tools;

namespace MrArchitectureReview.Tests;

public sealed class BuildingBlockTests
{
    private static string FixtureRepo => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SamplePr", "repo");

    [Theory]
    [InlineData("https://github.com/microsoft/agent-framework/pull/1234", "microsoft", "agent-framework", 1234)]
    [InlineData("https://github.com/leonio/Agent-Framework-Samples/pull/7/", "leonio", "Agent-Framework-Samples", 7)]
    public void PullRequestUrl_parses_github_urls(string url, string owner, string repo, int number)
    {
        Assert.True(PullRequestUrl.TryParse(url, out var parsed));
        Assert.Equal(new PullRequestUrl(owner, repo, number), parsed);
    }

    [Theory]
    [InlineData("sample")]
    [InlineData("https://gitlab.com/group/project/-/merge_requests/5")]
    [InlineData("https://github.com/owner/repo/issues/5")]
    public void PullRequestUrl_rejects_everything_else(string value) =>
        Assert.False(PullRequestUrl.TryParse(value, out _));

    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("src/../../outside.txt")]
    public void RepositoryTools_refuse_paths_outside_the_checkout(string path)
    {
        var tools = new RepositoryTools(FixtureRepo);
        Assert.StartsWith("ERROR", tools.ReadFile(path), StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryTools_read_and_search_inside_the_checkout()
    {
        var tools = new RepositoryTools(FixtureRepo);
        Assert.Contains("interface IPaymentGateway", tools.ReadFile("src/Infrastructure/Payments/IPaymentGateway.cs"), StringComparison.Ordinal);
        Assert.Contains("StripeGateway.cs", tools.SearchCode("IPaymentGateway"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuidelinesSearch_retrieves_sections_whose_keywords_appear_in_the_diff()
    {
        var search = new GuidelinesSearch();
        var hits = (await search.SearchAsync("var http = new HttpClient(); var cmd = new SqlCommand(...)", TestContext.Current.CancellationToken)).ToList();

        Assert.Contains(hits, h => h.SourceName!.EndsWith("#HTTP clients", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.SourceName!.EndsWith("#Data access", StringComparison.Ordinal));
    }

    [Fact]
    public void Finding_ids_are_assigned_by_the_workflow_and_confidence_is_clamped()
    {
        var draft = new FindingDraft("t", "d", "r", Severity.High, null, Confidence: 85);
        var finding = Finding.From(draft, ReviewAspect.Security, ordinal: 2);

        Assert.Equal("SEC-2", finding.Id);
        Assert.Equal(0.85, finding.Confidence, precision: 3);
    }
}
