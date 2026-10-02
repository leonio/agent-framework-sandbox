using Roster.Platform.Data;

namespace Roster.Platform.Queue;

/// <summary>
/// The work queue between the API (which enqueues) and the runners (which claim and execute). One implementation in
/// slice 1, <see cref="PostgresJobQueue"/>; the interface is the seam for RabbitMQ or NATS later (design doc D5).
/// </summary>
/// <remarks>
/// <para>A job's life: <b>enqueued</b> (state Queued) → <b>claimed</b> by one runner, which holds a lease (Running) →
/// the runner renews the lease with <b>heartbeats</b> while it works → <b>completed</b> (Succeeded) or <b>failed</b>.
/// A failed job goes back to Queued with a growing delay until it runs out of attempts, then it is Dead. If a runner
/// dies, its lease runs out and another runner claims the job again.</para>
/// <para>Handlers must therefore tolerate running twice. The ledger is keyed by phase and attempt, and the scenario
/// skips phases that already succeeded.</para>
/// </remarks>
public interface IJobQueue
{
    /// <summary>
    /// Adds a job to <paramref name="db"/> without saving, so it commits in the same transaction as the caller's own
    /// changes (an assignment and its first job appear together or not at all).
    /// </summary>
    Job Add(RosterDb db, string kind, object payload, EnqueueOptions? options = null);

    /// <summary>Enqueues on its own. With an idempotency key, a second call returns the existing job's id.</summary>
    Task<Guid> EnqueueAsync(string kind, object payload, EnqueueOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Claims the oldest due job in <paramref name="pool"/>, or returns null if there is none.</summary>
    Task<ClaimedJob?> ClaimAsync(string pool, string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Extends the lease. False means the lease was lost (it expired and someone else took the job): stop work.</summary>
    Task<bool> HeartbeatAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default);

    /// <summary>Records a failure. Retries with back-off unless <paramref name="retry"/> is false or attempts are used up.</summary>
    Task FailAsync(Guid jobId, string workerId, string error, bool retry = true, CancellationToken cancellationToken = default);
}

public sealed record EnqueueOptions(
    string Pool = "general",
    string? IdempotencyKey = null,
    DateTimeOffset? NotBefore = null,
    int MaxAttempts = 3);

/// <summary>What a runner gets when it claims a job. <see cref="IsLastAttempt"/> tells handlers whether a failure now is final.</summary>
public sealed record ClaimedJob(Guid Id, string Kind, string PayloadJson, int Attempt, int MaxAttempts, string? TraceParent)
{
    public bool IsLastAttempt => Attempt >= MaxAttempts;
}

/// <summary>Payload of a <see cref="JobKinds.PhaseRun"/> job: run this phase of this assignment.</summary>
public sealed record PhaseRunPayload(Guid AssignmentId, Guid PhaseId);

/// <summary>Payload of a <see cref="JobKinds.RetroTurn"/> job: let the facilitator answer in this retro.</summary>
public sealed record RetroTurnPayload(Guid SessionId);
