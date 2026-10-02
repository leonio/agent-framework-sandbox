using System.Text.Json;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace CliDesignPipeline.Workflow.Executors;

/// <summary>
/// Step 2: the developer agent writes the app into the run workspace using file tools.
/// Handles both the first build (<see cref="ApprovedSpec"/>) and rework (<see cref="ReviewOutcome"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Code goes through tools, not through the answer.</b> We could ask the model to return
/// <c>{ files: [{ path, content }] }</c> and write them ourselves. Tool calls are better here:
/// the agent can read what exists before editing (essential on rework), each write is validated
/// (path sandbox, size limit) and observable, and the final answer stays a small summary rather
/// than a 20&#160;KB JSON blob that is likely to be truncated or malformed.
/// </para>
/// <para>
/// <b>Ground truth from disk.</b> The message we pass on lists the files that are <i>actually</i>
/// in the workspace (<see cref="RunWorkspace.ListAppFiles"/>), not the ones the model
/// <i>says</i> it wrote. Trust tools' side effects over models' claims about them.
/// </para>
/// <para>
/// One session across rounds: on rework the developer remembers what it built and why, so the
/// fix is a targeted edit rather than a rewrite. The trade-off is context growth; for large
/// apps start each round with a fresh session plus a summary instead.
/// </para>
/// </remarks>
internal sealed class DeveloperExecutor(AIAgent developer, RunWorkspace workspace) : Executor("developer")
{
    private AgentSession? _session;

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        protocol
            .ConfigureRoutes(routes => routes
                .AddHandler<ApprovedSpec>((spec, ctx, ct) => BuildAsync(
                    round: 1,
                    $"Implement this approved requirements specification:\n```json\n{JsonSerializer.Serialize(spec.Spec, PipelineJson.Options)}\n```",
                    ctx, ct))
                .AddHandler<ReviewOutcome>((review, ctx, ct) => BuildAsync(
                    round: review.Round + 1,
                    $"Review report from the Tester (round {review.Round}). Fix the findings:\n```json\n{JsonSerializer.Serialize(review.Report, PipelineJson.Options)}\n```",
                    ctx, ct)))
            .SendsMessage<ImplementationRound>();

    private async ValueTask BuildAsync(int round, string prompt, IWorkflowContext context, CancellationToken ct)
    {
        await context.AddEventAsync(new StageEvent("develop", round == 1 ? "Implementing the spec..." : $"Rework round {round}: fixing review findings..."), ct);

        _session ??= await developer.CreateSessionAsync(ct);
        var response = await developer.RunAsync<DeveloperReport>(prompt, _session, PipelineJson.Options, cancellationToken: ct);

        var files = workspace.ListAppFiles();
        await context.AddEventAsync(new StageEvent("develop", $"{response.Result.Summary} ({files.Count} files in workspace)"), ct);
        await context.SendMessageAsync(new ImplementationRound(round, response.Result, files), ct);
    }
}
