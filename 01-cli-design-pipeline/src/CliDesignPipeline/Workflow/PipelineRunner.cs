using System.Diagnostics;
using CliDesignPipeline.Cli;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using Microsoft.Agents.AI.Workflows;

namespace CliDesignPipeline.Workflow;

/// <summary>
/// Hosts one workflow run: starts it, streams its events, answers human requests, and reports
/// the outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming vs non-streaming.</b> <c>InProcessExecution.RunStreamingAsync</c> + <c>WatchStreamAsync</c>
/// gives us every event as it happens, and the stream simply waits while a request is
/// outstanding until we call <c>SendResponseAsync</c>. That suits an interactive console. The
/// non-streaming <c>RunAsync</c> instead returns at the first pending request; you collect
/// answers and call <c>ResumeAsync</c>. That suits request/response hosts such as a web API
/// (sample 02), especially combined with a <c>CheckpointManager</c> so the run can be resumed
/// by a different process later.
/// </para>
/// </remarks>
public sealed class PipelineRunner(DesignPipelineWorkflow pipeline, IStakeholder stakeholder, RunWorkspace workspace, ConsoleUi ui)
{
    public async Task<int> RunAsync(DesignBrief brief, CancellationToken ct)
    {
        var workflow = await pipeline.BuildAsync(ct);
        await workspace.WriteArtifactAsync("workflow.mmd", workflow.ToMermaidString(), ct);

        ui.Banner("CLI design pipeline", $"Run folder: {workspace.Root}");
        var clock = Stopwatch.StartNew();
        MergeRequestPlan? result = null;

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, brief, cancellationToken: ct);

        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case RequestInfoEvent { Request: var request }:
                    await run.SendResponseAsync(await AnswerAsync(request, ct));
                    break;

                case StageEvent stage:
                    ui.Stage(stage.Stage, stage.Message);
                    break;

                case WorkflowOutputEvent output when output.Is<MergeRequestPlan>(out var plan):
                    result = plan;
                    break;

                case ExecutorFailedEvent failed:
                    ui.Error($"Step '{failed.ExecutorId}' failed: {failed.Data?.Message}");
                    return 1;

                case WorkflowErrorEvent error:
                    ui.Error($"Workflow error: {error.Exception?.Message}");
                    return 1;
            }
        }

        if (result is null)
        {
            ui.Warn("The workflow finished without producing a merge request.");
            return 1;
        }

        ui.Banner("Merge request", $"Finished in {clock.Elapsed:mm\\:ss}");
        ui.Markdown(result.ToMarkdown());
        ui.Success($"Everything is in {workspace.Root}");
        return 0;
    }

    /// <summary>
    /// Requests carry their payload as a <c>PortableValue</c>; we match on the payload type to
    /// decide who answers. One switch arm per request port.
    /// </summary>
    private async ValueTask<ExternalResponse> AnswerAsync(ExternalRequest request, CancellationToken ct)
    {
        if (request.TryGetDataAs<InterviewQuestion>(out var question))
        {
            return request.CreateResponse(await stakeholder.AnswerAsync(question, ct));
        }

        if (request.TryGetDataAs<SpecApprovalRequest>(out var approval))
        {
            return request.CreateResponse(await stakeholder.ReviewSpecAsync(approval, ct));
        }

        throw new NotSupportedException($"No handler for request on port '{request.PortInfo}'.");
    }
}
