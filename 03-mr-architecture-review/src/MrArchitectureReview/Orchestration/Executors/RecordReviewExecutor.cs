using Microsoft.Agents.AI.Workflows;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Persistence;

namespace MrArchitectureReview.Orchestration.Executors;

/// <summary>
/// Step 5 (no AI): persist the AI findings and the human decisions side by side.
/// </summary>
/// <remarks>
/// Deliberately <i>before</i> publishing: if Jira or GitHub is down, the triage work a human just did is
/// already safe on disk. Order side effects from "most precious" to "easiest to redo".
/// </remarks>
public sealed class RecordReviewExecutor(ReviewStore store) : Executor<ReviewRecord, RecordedReview>("record-review")
{
    public override async ValueTask<RecordedReview> HandleAsync(ReviewRecord record, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var path = await store.SaveAsync(record, cancellationToken);
        await context.AddEventAsync(new ReviewProgressEvent(
            $"Saved review record ({record.AcceptedCount} accepted, {record.RejectedCount} rejected) to {path}"), cancellationToken);
        return new RecordedReview(record, path);
    }
}
