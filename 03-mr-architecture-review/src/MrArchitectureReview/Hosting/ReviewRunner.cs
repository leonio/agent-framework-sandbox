using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Orchestration;

namespace MrArchitectureReview.Hosting;

/// <summary>
/// Runs one review: starts the workflow, relays progress, answers human-triage requests, returns the output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streaming vs non-streaming execution.</b> <c>InProcessExecution.RunStreamingAsync</c> gives us an event
/// stream we can react to while the workflow runs, which is required for human-in-the-loop: a
/// <see cref="RequestInfoEvent"/> arrives, we answer it with <c>SendResponseAsync</c>, and the workflow
/// continues. <c>InProcessExecution.RunAsync</c> runs to the first point where it needs input and returns,
/// better for request/response hosts (e.g. a web API that stores pending requests and resumes later).
/// </para>
/// <para>
/// <b>"InProcess"</b> means the workflow runs inside this process. The same <c>Workflow</c> definition can be
/// hosted durably (e.g. on Azure Durable Functions / Durable Task) so a triage that takes days survives
/// restarts. Checkpointing (<c>CheckpointManager</c>) is the in-process stepping stone to that.
/// </para>
/// </remarks>
public sealed class ReviewRunner(ReviewWorkflowFactory workflows, ITriageHandler triage, ILogger<ReviewRunner> logger)
{
    public async Task<PublishOutcome> RunAsync(ReviewRequest request, CancellationToken cancellationToken = default)
    {
        var workflow = workflows.Build();
        await using var run = await InProcessExecution.RunStreamingAsync(workflow, request, cancellationToken: cancellationToken);

        PublishOutcome? outcome = null;
        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            switch (evt)
            {
                case ReviewProgressEvent progress:
                    logger.LogInformation("{Message}", progress.Message);
                    break;

                // The workflow is asking the outside world for something. We only have one port, but a
                // workflow can have many, so check which one (PortInfo.PortId) and the payload type.
                case RequestInfoEvent { Request: var req } when req.TryGetDataAs<TriageRequest>(out var triageRequest):
                    var decision = await triage.DecideAsync(triageRequest, cancellationToken);
                    await run.SendResponseAsync(req.CreateResponse(decision));
                    break;

                case WorkflowOutputEvent output when output.Is<PublishOutcome>(out var result):
                    outcome = result;
                    break;

                case ExecutorFailedEvent failed:
                    throw new InvalidOperationException($"Executor '{failed.ExecutorId}' failed.", failed.Data as Exception);

                case WorkflowErrorEvent error:
                    throw new InvalidOperationException("Workflow failed.", error.Data as Exception);
            }
        }

        return outcome ?? throw new InvalidOperationException(
            "Workflow finished without an output. Check the log for an executor that did not send a message.");
    }
}
