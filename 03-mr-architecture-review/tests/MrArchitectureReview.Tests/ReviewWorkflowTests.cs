using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Hosting;
using MrArchitectureReview.Persistence;

namespace MrArchitectureReview.Tests;

/// <summary>
/// End-to-end tests of the whole workflow with the scripted model and policy triage.
/// </summary>
/// <remarks>
/// This is the payoff of the offline <c>ScriptedChatClient</c>: agents, tools, context providers, the
/// workflow graph, the human-in-the-loop port and persistence all run for real, deterministically,
/// in milliseconds. What these tests deliberately do <i>not</i> check is review quality; that needs an
/// evaluation run against a real model (see README, "Evaluating prompt changes").
/// </remarks>
public sealed class ReviewWorkflowTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mr-review-tests-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<ReviewOptions>(o =>
        {
            o.Ai.Provider = AiProvider.Mock;
            o.Storage.DataDirectory = _dataDir;
            o.AutoTriage = true;
            o.Reviewer = "test";
        });
        services.AddMrArchitectureReview();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Sample_PR_runs_end_to_end_and_persists_findings_with_decisions()
    {
        await using var sp = BuildServices();

        var outcome = await sp.GetRequiredService<ReviewRunner>()
            .RunAsync(new ReviewRequest("sample"), TestContext.Current.CancellationToken);

        var record = outcome.Record;
        Assert.Equal("contoso/shop-api", record.RepositoryKey);
        Assert.Equal(3, record.Aspects.Count);                        // all reviewers reported (fan-in)
        Assert.Equal(9, record.Findings.Count);                       // 3 design + 4 security + 2 extensibility
        Assert.All(record.Findings, f => Assert.Equal(f.Finding.Id, f.Decision.FindingId));
        Assert.Equal(Severity.High, record.Findings[0].Finding.Severity); // ordered most severe first
        Assert.True(File.Exists(outcome.RecordPath));
        Assert.True(File.Exists(Path.ChangeExtension(outcome.RecordPath, ".md")));
        Assert.Contains("Jira", outcome.PublisherSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decisions_from_a_previous_run_are_available_as_reviewer_history()
    {
        await using var sp = BuildServices();
        await sp.GetRequiredService<ReviewRunner>().RunAsync(new ReviewRequest("sample"), TestContext.Current.CancellationToken);

        var history = await sp.GetRequiredService<ReviewStore>()
            .GetHistoryAsync("contoso/shop-api", ReviewAspect.Design, max: 10, TestContext.Current.CancellationToken);

        Assert.Equal(3, history.Count);
        // Rejections with a reason are listed first: they carry the most signal for the next review.
        Assert.Equal(Verdict.Rejected, history[0].Decision.Verdict);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }
}
