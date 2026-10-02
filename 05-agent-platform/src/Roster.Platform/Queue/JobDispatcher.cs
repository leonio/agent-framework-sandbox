using System.Diagnostics;
using System.Text.Json;
using Roster.Agents;
using Roster.Platform.Data;
using Roster.Platform.Retro;
using Roster.Platform.Scenarios;

namespace Roster.Platform.Queue;

/// <summary>
/// Runs a claimed job: the runner host's claim loop hands every job here. Keeps the runner itself a thin loop of
/// claim, heartbeat, dispatch, complete or fail.
/// </summary>
/// <remarks>
/// Each job continues the trace of whoever enqueued it (the job row's <c>traceparent</c>), so in the Aspire dashboard
/// one trace runs from the API request through the queue to the runner, the model calls and the tools.
/// </remarks>
public sealed class JobDispatcher(ScenarioEngine engine, RetroService retro)
{
    /// <summary>The OpenTelemetry source for job spans. Hosts add it to their tracing.</summary>
    public const string ActivitySourceName = "Roster.Jobs";

    private static readonly ActivitySource s_source = new(ActivitySourceName);

    public async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ActivityContext.TryParse(job.TraceParent, traceState: null, out ActivityContext parent);
        using Activity? activity = s_source.StartActivity($"job {job.Kind}", ActivityKind.Consumer, parent);
        activity?.SetTag("roster.job.id", job.Id);
        activity?.SetTag("roster.job.attempt", job.Attempt);

        switch (job.Kind)
        {
            case JobKinds.PhaseRun:
                PhaseRunPayload phase = Read<PhaseRunPayload>(job);
                activity?.SetTag("roster.assignment.id", phase.AssignmentId);
                await engine.RunPhaseAsync(phase, job.IsLastAttempt, cancellationToken);
                break;

            case JobKinds.RetroTurn:
                RetroTurnPayload turn = Read<RetroTurnPayload>(job);
                activity?.SetTag("roster.retro.session_id", turn.SessionId);
                await retro.RunTurnAsync(turn, job.Attempt, job.IsLastAttempt, cancellationToken);
                break;

            default:
                throw new NotSupportedException($"No handler for job kind '{job.Kind}'.");
        }
    }

    private static T Read<T>(ClaimedJob job) =>
        JsonSerializer.Deserialize<T>(job.PayloadJson, ContractJson.Options)
        ?? throw new InvalidDataException($"Job {job.Id} has an empty {typeof(T).Name}.");
}
