using System.Text.Json;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace CliDesignPipeline.Workflow.Executors;

/// <summary>
/// Step 3: the tester reviews the code against the spec, writes tests, and decides whether the
/// work goes back to the developer or on to release.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model gives a verdict; code decides the route.</b> The tester returns
/// <see cref="ReviewVerdict"/>, but whether we loop again is <see cref="NextStep"/>, computed here
/// from the verdict <i>and</i> the round budget. The workflow edges then route on
/// <see cref="NextStep"/> only. Splitting it this way keeps the policy ("max N rounds, then ship
/// with findings noted") in one testable place instead of hidden in an edge lambda or a prompt.
/// </para>
/// <para>
/// Alternatives for the routing itself: conditional edges (used - see <c>DesignPipelineWorkflow</c>),
/// <c>AddSwitch(...)</c> for many-way branches, or a fan-out with a target selector.
/// </para>
/// <para>
/// <b>Fresh session per round.</b> Unlike the developer, the tester starts clean every round
/// so it re-checks <i>everything</i> against the spec, instead of only verifying last round's
/// findings and anchoring on its earlier opinion. Independent re-review is the point of a tester.
/// </para>
/// </remarks>
internal sealed class TesterExecutor(AIAgent tester, RunWorkspace workspace, int maxRounds)
    : Executor<ImplementationRound>("tester")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        base.ConfigureProtocol(protocol).SendsMessage<ReviewOutcome>();

    public override async ValueTask HandleAsync(ImplementationRound round, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.AddEventAsync(new StageEvent("test", $"Reviewing round {round.Round} ({round.FilesOnDisk.Count} files)..."), cancellationToken);

        // Shared state written by the interviewer after approval (see PipelineState).
        var spec = await context.ReadStateAsync<RequirementsSpec>(PipelineState.Spec, PipelineState.Scope, cancellationToken)
            ?? throw new InvalidOperationException("No approved spec in workflow state.");

        var prompt = $"""
            Review round {round.Round} of {maxRounds}.

            ## Specification
            ```json
            {JsonSerializer.Serialize(spec, PipelineJson.Options)}
            ```

            ## Developer's summary
            {round.Report.Summary}

            ## Files in the workspace
            {string.Join(Environment.NewLine, round.FilesOnDisk.Select(f => $"- {f}"))}
            """;

        var session = await tester.CreateSessionAsync(cancellationToken);
        var report = (await tester.RunAsync<TesterReport>(prompt, session, PipelineJson.Options, cancellationToken: cancellationToken)).Result;

        var next = report.Verdict switch
        {
            ReviewVerdict.Approved => NextStep.Ship,
            ReviewVerdict.ChangesRequested when round.Round < maxRounds => NextStep.Rework,
            _ => NextStep.Ship, // out of rounds: ship anyway, the open findings go in the MR for a human
        };

        var outcome = new ReviewOutcome(round.Round, report, next);
        await workspace.WriteArtifactAsync($"REVIEW-{round.Round}.md", outcome.ToMarkdown(), cancellationToken);
        await context.AddEventAsync(new StageEvent("test",
            $"{report.Verdict} with {report.Findings.Count} finding(s) [{string.Join(", ", report.Findings.Select(f => f.Severity))}] -> {next}"), cancellationToken);

        await context.SendMessageAsync(outcome, cancellationToken);
    }
}
