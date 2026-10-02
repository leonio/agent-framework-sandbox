using Microsoft.Agents.AI.Workflows;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Orchestration.Executors;

/// <summary>
/// Step 3 (no AI): fan-in. Waits for every reviewer, then de-duplicates and orders the findings.
/// </summary>
/// <remarks>
/// <para>
/// <b>How fan-in works.</b> The edge is declared with <c>AddFanInBarrierEdge([design, security, extensibility], this)</c>.
/// The barrier holds messages until <i>all</i> sources have sent one, then delivers them together in a
/// single superstep: our handler is called once per <see cref="AspectReview"/>, back to back. We collect
/// them and emit once we have the expected count.
/// </para>
/// <para>
/// <b>Instance state is safe here</b> because <see cref="ReviewWorkflowFactory"/> builds a fresh workflow (and
/// fresh executors) per run. If you share one <c>Workflow</c> across concurrent runs, either keep this
/// state in workflow state (<c>context.QueueStateUpdateAsync</c>) or register executors through a factory
/// binding so each run gets its own instance.
/// </para>
/// <para>
/// <b>Alternative:</b> an LLM "judge/aggregator" agent that merges overlapping findings semantically
/// ("controller does data access" and "no test seam" are related). Better merges, but another model call
/// and less predictable. The deterministic version is a good default; add the judge when duplicates
/// actually annoy people (the reject reasons in the decision log will tell you).
/// </para>
/// </remarks>
public sealed class ConsolidateFindingsExecutor(int expectedReviews) : Executor("consolidate-findings")
{
    private readonly List<AspectReview> _received = [];

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<AspectReview>(HandleAsync))
            .SendsMessage<ConsolidatedReview>();

    private async ValueTask HandleAsync(AspectReview review, IWorkflowContext context, CancellationToken cancellationToken)
    {
        _received.Add(review);
        if (_received.Count < expectedReviews)
            return;

        var runInfo = await context.ReadStateAsync<RunInfo>(StateKeys.RunInfo, StateKeys.Scope, cancellationToken)
                      ?? throw new InvalidOperationException("Run info missing from workflow state");

        List<Finding> findings = [.. _received
            .SelectMany(r => r.Findings)
            .DistinctBy(f => (f.FilePath, Normalise(f.Title)))
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Confidence)];

        await context.AddEventAsync(new ReviewProgressEvent(
            $"Consolidated {findings.Count} finding(s) from {_received.Count} reviewers"), cancellationToken);

        await context.SendMessageAsync(
            new ConsolidatedReview(runInfo.RunId, [.. _received.OrderBy(r => r.Aspect)], findings),
            cancellationToken: cancellationToken);
    }

    private static string Normalise(string title) =>
        new([.. title.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
