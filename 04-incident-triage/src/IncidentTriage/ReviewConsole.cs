using IncidentTriage.Persistence;
using IncidentTriage.Workflow;

namespace IncidentTriage;

/// <summary>
/// The console implementation of the two human gates. A web host would render the same requests as forms;
/// the workflow neither knows nor cares, it only sees a <see cref="ReviewDecision"/> / <see cref="PublishDecision"/> come back.
/// </summary>
internal static class ReviewConsole
{
    public static ReviewDecision AskRootCause(RootCauseReviewRequest review, string reviewer, bool autoApprove)
    {
        var (cluster, h) = (review.Cluster, review.Hypothesis);

        ConsoleUi.Heading($"Review {cluster.ClusterId} ({cluster.Severity}) round {review.Round}/{review.MaxRounds}: {cluster.Title}");
        ConsoleUi.Info($"Root cause   : {h.Summary}");
        ConsoleUi.Info($"Category     : {h.Category}   Suspect: {h.SuspectedComponent}   Confidence: {h.Confidence:P0}");
        ConsoleUi.Info("Evidence     :");
        foreach (var e in h.Evidence) ConsoleUi.Info($"  - [{e.Source}] {e.Detail}");
        ConsoleUi.Info("Mitigation   :");
        foreach (var m in h.Mitigations) ConsoleUi.Info($"  - {m}");

        if (autoApprove) return new ReviewDecision(ReviewVerdict.Accept, "auto-approved (--yes)", reviewer);

        while (true)
        {
            var answer = ConsoleUi.Ask("[a]ccept, [r]eject, [s]kip", "a").ToLowerInvariant();
            ReviewVerdict? verdict = answer switch
            {
                "a" or "accept" => ReviewVerdict.Accept,
                "r" or "reject" => ReviewVerdict.Reject,
                "s" or "skip" => ReviewVerdict.Skip,
                _ => null,
            };
            if (verdict is null) continue;

            // The reason is the valuable part: it is fed back to the agent on a rejection and stored in the
            // journal either way. Required for rejections, optional (but encouraged) otherwise.
            var reason = ConsoleUi.Ask(verdict == ReviewVerdict.Reject ? "Why is it wrong? (sent back to the agent)" : "Why? (optional, stored with the decision)", "");
            if (verdict == ReviewVerdict.Reject && string.IsNullOrWhiteSpace(reason))
            {
                ConsoleUi.Warn("A rejection needs a reason so the agent can do better.");
                continue;
            }
            return new ReviewDecision(verdict.Value, reason, reviewer);
        }
    }

    public static PublishDecision AskPublish(PublishApprovalRequest request, string reviewer, bool autoApprove)
    {
        ConsoleUi.Heading("Publish drafts?");
        foreach (var issue in request.Issues)
            ConsoleUi.Info($"  Jira   [{issue.Priority}] {issue.Summary}{(issue.RelatedIssueKeys.Count > 0 ? $"  (related: {string.Join(", ", issue.RelatedIssueKeys)})" : "")}");
        foreach (var page in request.Pages)
            ConsoleUi.Info($"  Wiki   {page.Title}");
        ConsoleUi.Info($"  Drafts are in {request.OutputFolder}");

        if (autoApprove) return new PublishDecision(true, "auto-approved (--yes)", reviewer);

        var approved = ConsoleUi.Ask("Create these in Jira and Confluence (dry-run unless configured)? [y/n]", "y").StartsWith('y');
        var reason = ConsoleUi.Ask("Why? (optional, stored with the decision)", "");
        return new PublishDecision(approved, reason, reviewer);
    }

    public static void PrintSummary(TriageReport report, TriageJournal journal)
    {
        ConsoleUi.Heading("Result");
        foreach (var i in report.Incidents)
        {
            var rounds = i.History.Count;
            ConsoleUi.Info($"{i.Cluster.ClusterId} {i.FinalVerdict,-6} after {rounds} round(s): {i.Hypothesis.Summary}");
            foreach (var r in i.History.Where(r => !string.IsNullOrWhiteSpace(r.Decision.Reason)))
                ConsoleUi.Info($"    round {r.Round} {r.Decision.Verdict}: \"{r.Decision.Reason}\"");
        }
        ConsoleUi.Info(report.Published
            ? $"Published: {string.Join(", ", report.CreatedIssueKeys)}  |  {string.Join(", ", report.CreatedPageUrls)}"
            : "Not published.");
        ConsoleUi.Info($"Artefacts and journal: {report.OutputFolder}");
        ConsoleUi.Info($"Journal entries: {journal.Entries.Count} ({journal.Entries.Count(e => e.Kind == JournalKind.HumanDecision)} human decisions, {journal.Entries.Count(e => e.Kind == JournalKind.ToolCall)} tool calls)");
    }
}
