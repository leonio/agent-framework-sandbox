using Microsoft.Agents.AI.Workflows;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Orchestration.Executors;

/// <summary>
/// Step 4 (human in the loop): asks a person to accept or reject each finding, one at a time, and
/// records their reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Request ports.</b> A <c>RequestPort&lt;TriageRequest, TriageDecision&gt;</c> is a node in the graph that
/// represents "something outside the workflow". When this executor sends a <see cref="TriageRequest"/>
/// to the port, the workflow emits a <c>RequestInfoEvent</c> to the host and pauses that branch. The host
/// (console, web UI, Teams card, e-mail...) answers with <c>run.SendResponseAsync(...)</c>, and the
/// <see cref="TriageDecision"/> arrives back here as an ordinary message.
/// </para>
/// <para>
/// The graph has a cycle: <c>coordinator -&gt; port -&gt; coordinator</c>. Cycles are fine in this
/// framework (it runs in supersteps, Pregel-style), and they are the idiomatic way to loop.
/// </para>
/// <para>
/// <b>WHY one request per finding instead of one big form?</b> It keeps the request/response types tiny and
/// lets a UI show one card at a time. It also means that with checkpointing enabled you can stop after
/// finding 3 of 8 and resume tomorrow without losing decisions (pass a <c>CheckpointManager</c> to
/// <c>InProcessExecution.RunStreamingAsync</c>, then <c>ResumeStreamingAsync</c> from the last checkpoint).
/// Alternative: send the whole list in one request, which is simpler for batch UIs.
/// </para>
/// <para>
/// <b>Why record a reason?</b> An accept/reject bit tells you the AI was wrong; the reason tells you
/// <i>why</i>, and it is fed back into future reviews (see ReviewHistoryContextProvider) and is the label
/// for evaluating prompt changes.
/// </para>
/// </remarks>
public sealed class TriageCoordinatorExecutor(string reviewer, string modelProvider, string modelName) : Executor("triage-coordinator")
{
    private ConsolidatedReview? _review;
    private readonly List<TriagedFinding> _decided = [];

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .ConfigureRoutes(routes => routes
                .AddHandler<ConsolidatedReview>(StartAsync)
                .AddHandler<TriageDecision>(OnDecisionAsync))
            .SendsMessage<TriageRequest>()
            .SendsMessage<ReviewRecord>();

    private async ValueTask StartAsync(ConsolidatedReview review, IWorkflowContext context, CancellationToken cancellationToken)
    {
        _review = review;
        _decided.Clear();
        await AskNextOrFinishAsync(context, cancellationToken);
    }

    private async ValueTask OnDecisionAsync(TriageDecision decision, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var review = _review ?? throw new InvalidOperationException("Decision received before any review.");
        var finding = review.Findings[_decided.Count];

        // Guard against a host answering the wrong request (e.g. a stale UI tab).
        if (decision.FindingId != finding.Id)
            throw new InvalidOperationException($"Expected a decision for {finding.Id}, got {decision.FindingId}.");

        _decided.Add(new TriagedFinding(finding, decision with { DecidedBy = string.IsNullOrWhiteSpace(decision.DecidedBy) ? reviewer : decision.DecidedBy }));
        await AskNextOrFinishAsync(context, cancellationToken);
    }

    private async ValueTask AskNextOrFinishAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        var review = _review!;
        if (_decided.Count < review.Findings.Count)
        {
            var next = review.Findings[_decided.Count];
            await context.SendMessageAsync(new TriageRequest(next, _decided.Count + 1, review.Findings.Count), cancellationToken: cancellationToken);
            return;
        }

        var snapshot = await context.ReadStateAsync<PullRequestSnapshot>(StateKeys.Snapshot, StateKeys.Scope, cancellationToken)
                       ?? throw new InvalidOperationException("Snapshot missing from workflow state");
        var run = await context.ReadStateAsync<RunInfo>(StateKeys.RunInfo, StateKeys.Scope, cancellationToken)
                  ?? throw new InvalidOperationException("Run info missing from workflow state");

        await context.SendMessageAsync(new ReviewRecord(
            run.RunId, run.StartedAt, DateTimeOffset.UtcNow,
            snapshot.RepositoryKey, snapshot.Number, snapshot.Title, snapshot.HeadRef,
            modelProvider, modelName,
            review.Aspects, [.. _decided]), cancellationToken: cancellationToken);
    }
}
