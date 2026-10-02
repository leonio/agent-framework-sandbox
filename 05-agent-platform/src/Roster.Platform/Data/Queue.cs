namespace Roster.Platform.Data;

/// <summary>The job kinds runners know how to handle.</summary>
public static class JobKinds
{
    /// <summary>Run one phase of an assignment. Payload: <see cref="Queue.PhaseRunPayload"/>.</summary>
    public const string PhaseRun = "phase.run";

    /// <summary>Run one retro facilitator turn. Payload: <see cref="Queue.RetroTurnPayload"/>.</summary>
    public const string RetroTurn = "retro.turn";
}

public enum JobState
{
    /// <summary>Waiting to be claimed (once <see cref="Job.NotBefore"/> has passed).</summary>
    Queued,

    /// <summary>Claimed by a runner, which holds a lease and renews it with heartbeats.</summary>
    Running,

    Succeeded,

    /// <summary>Failed for good: out of attempts. Kept for the record.</summary>
    Dead,
}

/// <summary>
/// A row in the work queue. The queue is just this table: a runner claims the oldest due job for its pool with
/// <c>FOR UPDATE SKIP LOCKED</c>, so many runners can claim at once without blocking each other or taking the same job.
/// See <see cref="Queue.PostgresJobQueue"/> for the whole life cycle.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }

    /// <summary>What to do: one of <see cref="JobKinds"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>Which runner pool may take it (<c>general</c>, later <c>sandbox</c>).</summary>
    public string Pool { get; set; } = "general";

    /// <summary>Ids and parameters as JSON. Never secrets: runners load credentials themselves, just before use.</summary>
    public required string PayloadJson { get; set; }

    public JobState State { get; set; }

    /// <summary>How many times a runner has claimed it.</summary>
    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Not claimable before this time. Retries push it out with a growing back-off.</summary>
    public DateTimeOffset NotBefore { get; set; }

    /// <summary>The claiming runner's lease. If it passes without a heartbeat (the runner died), the job is claimable again.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>The worker id of the runner holding it.</summary>
    public string? LockedBy { get; set; }

    /// <summary>Optional: enqueueing the same key twice yields one job (a double-clicked button, a retried request).</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>The W3C trace context of whoever enqueued it, so one trace spans API, queue, runner and model calls.</summary>
    public string? TraceParent { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Something that happened in an assignment, for the live view: a phase started, a finding arrived, a retro message
/// grew. Runners insert a row and send a Postgres <c>NOTIFY</c>; every API replica <c>LISTEN</c>s and tells the browsers
/// watching that assignment, over server-sent events. Payloads carry ids and small facts; the UI refetches the rest.
/// </summary>
public sealed class RunEvent
{
    /// <summary>Increasing, so a browser that reconnects can ask for everything after the last id it saw.</summary>
    public long Id { get; set; }

    public Guid AssignmentId { get; set; }

    /// <summary>Such as <c>phase.started</c>, <c>findings.added</c>, <c>retro.message</c>. See <see cref="Queue.EventKinds"/>.</summary>
    public required string Kind { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}
