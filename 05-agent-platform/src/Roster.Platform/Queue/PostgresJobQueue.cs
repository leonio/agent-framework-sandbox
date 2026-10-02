using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Roster.Agents;
using Roster.Platform.Data;

namespace Roster.Platform.Queue;

/// <summary>
/// The job queue as a Postgres table (<c>jobs</c>), driven through EF Core. See <see cref="IJobQueue"/> for the life
/// cycle.
/// </summary>
/// <remarks>
/// <para>Everything is LINQ except the claim. Claiming must find the oldest due job and take it in one statement, and
/// skip rows other runners are taking at that same moment, which needs <c>FOR UPDATE SKIP LOCKED</c>. LINQ cannot say
/// that, so the claim is one raw SQL statement run through <c>FromSql</c>; it still returns an untracked
/// <see cref="Job"/> entity. Its column names are the snake_case ones from <see cref="RosterDb"/>.</para>
/// <para>Times come from this process's clock, passed as parameters, so every comparison uses one clock.</para>
/// <para>Comfortable into the low thousands of jobs a minute. Beyond that, swap the implementation behind the
/// interface (design doc section 7).</para>
/// </remarks>
public sealed class PostgresJobQueue(IDbContextFactory<RosterDb> dbs, ILogger<PostgresJobQueue> logger) : IJobQueue
{
    public Job Add(RosterDb db, string kind, object payload, EnqueueOptions? options = null)
    {
        options ??= new EnqueueOptions();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Pool = options.Pool,
            PayloadJson = JsonSerializer.Serialize(payload, ContractJson.Options),
            State = JobState.Queued,
            MaxAttempts = options.MaxAttempts,
            NotBefore = options.NotBefore ?? now,
            IdempotencyKey = options.IdempotencyKey,

            // The enqueuer's trace context (W3C traceparent). The runner continues the same trace from it.
            TraceParent = Activity.Current?.Id,
            CreatedAt = now,
        };

        db.Jobs.Add(job);
        return job;
    }

    public async Task<Guid> EnqueueAsync(string kind, object payload, EnqueueOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);

        // The usual repeat (a double click) is answered by a lookup. The unique index below catches the rare race.
        if (options?.IdempotencyKey is { } existingKey
            && await db.Jobs.Where(j => j.IdempotencyKey == existingKey).Select(j => (Guid?)j.Id).SingleOrDefaultAsync(cancellationToken) is { } existing)
        {
            return existing;
        }

        Job job = Add(db, kind, payload, options);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return job.Id;
        }
        catch (DbUpdateException ex) when (options?.IdempotencyKey is { } key && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Same idempotency key enqueued before: that job stands for this request too.
            await using RosterDb fresh = await dbs.CreateDbContextAsync(cancellationToken);
            return await fresh.Jobs.Where(j => j.IdempotencyKey == key).Select(j => j.Id).SingleAsync(cancellationToken);
        }
    }

    public async Task<ClaimedJob?> ClaimAsync(string pool, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset leaseUntil = now + lease;

        // A runner that died on its last attempt leaves a Running job whose lease ran out. It will not be retried
        // again, so mark it Dead first, or it would sit there forever.
        await db.Jobs
            .Where(j => j.Pool == pool && j.State == JobState.Running && j.LeaseUntil < now && j.Attempts >= j.MaxAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Dead)
                .SetProperty(j => j.LastError, "The runner stopped renewing its lease on the last attempt.")
                .SetProperty(j => j.CompletedAt, now), cancellationToken);

        // The claim, in one statement:
        //  - the inner SELECT finds the oldest due job in this pool that is queued, or running with an expired lease
        //    (its runner died) and attempts to spare;
        //  - FOR UPDATE SKIP LOCKED locks that row and makes other runners' identical statements skip it instead of
        //    waiting, so concurrent claims never block each other or take the same job;
        //  - the UPDATE takes the job (state, attempt count, lease, holder) and RETURNING hands back the row.
        // FromSql turns the interpolated values into parameters; nothing is concatenated into the SQL.
        string queued = nameof(JobState.Queued);
        string running = nameof(JobState.Running);
        List<Job> claimed = await db.Jobs.FromSql($"""
            UPDATE jobs
               SET state = {running}, attempts = attempts + 1, lease_until = {leaseUntil}, locked_by = {workerId}
             WHERE id = (
                   SELECT id FROM jobs
                    WHERE pool = {pool}
                      AND not_before <= {now}
                      AND (state = {queued} OR (state = {running} AND lease_until < {now} AND attempts < max_attempts))
                    ORDER BY not_before, created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);

        if (claimed is not [Job job])
        {
            return null;
        }

        logger.LogInformation("Worker {Worker} claimed {Kind} job {JobId} (attempt {Attempt})", workerId, job.Kind, job.Id, job.Attempts);
        return new ClaimedJob(job.Id, job.Kind, job.PayloadJson, job.Attempts, job.MaxAttempts, job.TraceParent);
    }

    public async Task<bool> HeartbeatAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        DateTimeOffset leaseUntil = DateTimeOffset.UtcNow + lease;

        // Only the current holder can extend the lease. Zero rows means someone else has the job now.
        int updated = await db.Jobs
            .Where(j => j.Id == jobId && j.LockedBy == workerId && j.State == JobState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, leaseUntil), cancellationToken);
        return updated == 1;
    }

    public async Task CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int updated = await db.Jobs
            .Where(j => j.Id == jobId && j.LockedBy == workerId && j.State == JobState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Succeeded)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), cancellationToken);

        if (updated == 0)
        {
            logger.LogWarning("Worker {Worker} finished job {JobId} after losing its lease; the result stands, the job may run again", workerId, jobId);
        }
    }

    public async Task FailAsync(Guid jobId, string workerId, string error, bool retry = true, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Job? job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId && j.LockedBy == workerId && j.State == JobState.Running, cancellationToken);
        if (job is null)
        {
            logger.LogWarning("Worker {Worker} could not record a failure for job {JobId}: it no longer holds the lease", workerId, jobId);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        job.LastError = error.Length > 4000 ? error[..4000] : error;
        job.LockedBy = null;
        job.LeaseUntil = null;

        if (!retry || job.Attempts >= job.MaxAttempts)
        {
            job.State = JobState.Dead;
            job.CompletedAt = now;
            logger.LogWarning("Job {JobId} ({Kind}) is dead after {Attempts} attempt(s): {Error}", job.Id, job.Kind, job.Attempts, error);
        }
        else
        {
            // Exponential back-off with jitter: about 5 s, 10 s, 20 s ... capped at 5 minutes.
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, job.Attempts - 1)) * (0.8 + Random.Shared.NextDouble() * 0.4));
            job.State = JobState.Queued;
            job.NotBefore = now + delay;
            logger.LogInformation("Job {JobId} ({Kind}) failed attempt {Attempt}; retrying in {Delay:F0} s", job.Id, job.Kind, job.Attempts, delay.TotalSeconds);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
