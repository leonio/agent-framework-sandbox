using System.Text;
using Microsoft.Agents.AI.Workflows;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Orchestration.Executors;

/// <summary>
/// Step 6 (AI + tools): an agent turns the triaged review into Jira tickets, a PR comment and, when a
/// rejection reason is a reusable rule, a Confluence decision-log entry.
/// </summary>
/// <remarks>
/// This is the only agent that can change anything outside the process, which is why it runs <i>after</i>
/// a human has triaged and only sees human-approved content. Its tools are dry-run by default
/// (see <c>Integrations/DryRunHttpHandler.cs</c>). Its return value is the workflow's final output
/// (configured with <c>WithOutputFrom</c> in <see cref="ReviewWorkflowFactory"/>).
/// </remarks>
public sealed class PublishExecutor(ReviewerAgentFactory agents) : Executor<RecordedReview, PublishOutcome>("publish")
{
    public override async ValueTask<PublishOutcome> HandleAsync(RecordedReview message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var record = message.Record;
        if (record.Findings.Count == 0)
            return new PublishOutcome(record, message.RecordPath, "Nothing to publish: no findings.");

        await context.AddEventAsync(new ReviewProgressEvent("Publisher agent recording the outcome (Jira, PR comment, decision log) ..."), cancellationToken);

        var agent = await agents.CreatePublisherAsync(cancellationToken);
        var response = await agent.RunAsync(Describe(record), cancellationToken: cancellationToken);

        return new PublishOutcome(record, message.RecordPath, response.Text);
    }

    private static string Describe(ReviewRecord r)
    {
        var (owner, repo) = r.RepositoryKey.Split('/') is [var o, var n] ? (o, n) : (r.RepositoryKey, r.RepositoryKey);
        var sb = new StringBuilder()
            .AppendLine($"Pull request: owner={owner} repo={repo} number={r.PullRequestNumber} title=\"{r.PullRequestTitle}\"")
            .AppendLine($"Review record: run {r.RunId}")
            .AppendLine()
            .AppendLine("Triaged findings:");

        foreach (var (f, d) in r.Findings.Select(x => (x.Finding, x.Decision)))
            sb.AppendLine($"- {f.Id} [{f.Severity}] {f.Title} => {d.Verdict}. Reason: {(d.Reason.Length > 0 ? d.Reason : "none")}. " +
                          $"File: {f.FilePath ?? "n/a"}. Recommendation: {f.Recommendation}");

        return sb.ToString();
    }
}
