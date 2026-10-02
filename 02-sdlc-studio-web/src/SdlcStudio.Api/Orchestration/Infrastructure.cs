using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Options;

namespace SdlcStudio.Api.Orchestration;

/// <summary>The unit of background work: "run this phase's workflow for this run".</summary>
public enum JobKind { InterviewTurn, Design, Import, Review, Revise, AgilePlan, DevPlan, DevelopStory, Testing }

public sealed record PhaseJob(Guid RunId, JobKind Kind);

/// <summary>A lightweight change notification pushed to the browser over Server-Sent Events.</summary>
public sealed record RunNotification(Guid RunId, string Kind, string Message);

/// <summary>
/// In-process queue of phase jobs.
/// </summary>
/// <remarks>
/// WHY a background queue? Agent workflows take seconds to minutes. HTTP requests should return
/// immediately ("202 Accepted, phase queued") and let the UI follow progress via events.
/// WHY System.Threading.Channels? It is the built-in, allocation-friendly producer/consumer primitive.
/// ALTERNATIVES for production: a durable queue (Azure Service Bus, RabbitMQ), Hangfire/Quartz, or Durable
/// Task orchestrations. Here durability comes from the database: the pending job is stored on the run and
/// re-queued at startup (see <see cref="PhaseWorker"/>).
/// </remarks>
public sealed class PhaseJobQueue
{
    private readonly Channel<PhaseJob> _channel = Channel.CreateUnbounded<PhaseJob>(new() { SingleReader = false });

    public ValueTask EnqueueAsync(PhaseJob job, CancellationToken ct = default) => _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<PhaseJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Fan-out of run notifications to connected browsers (one channel per subscriber).
/// </summary>
/// <remarks>
/// ALTERNATIVES: SignalR (bi-directional, scales out with a backplane), or plain polling. SSE is a good
/// fit here: one-way server-to-browser, works over plain HTTP, and .NET 10 has
/// <c>TypedResults.ServerSentEvents</c> built in. For several API instances you would need a shared
/// backplane (Redis pub/sub) because this hub is in memory.
/// </remarks>
public sealed class RunEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<RunNotification>> _subscribers = new();

    public void Publish(RunNotification notification)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(notification);
    }

    /// <param name="runId">Only events for this run, or <see cref="Guid.Empty"/> for every run (dashboard).</param>
    public async IAsyncEnumerable<RunNotification> SubscribeAsync(Guid runId, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<RunNotification>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        try
        {
            await foreach (var n in channel.Reader.ReadAllAsync(ct))
                if (runId == Guid.Empty || n.RunId == runId)
                    yield return n;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Writes the run's activity log. Uses its own short-lived DbContext per write because it is called from
/// workflow executors that may run in parallel (a DbContext must never be shared across threads).
/// </summary>
public sealed class RunJournal(IDbContextFactory<StudioDbContext> dbFactory, RunEventHub hub, ILogger<RunJournal> logger)
{
    public async Task WriteAsync(Guid runId, WorkflowPhase phase, string message, string level = "info", CancellationToken ct = default)
    {
        logger.LogInformation("[{RunId}] {Phase}: {Message}", runId, phase, message);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Events.Add(new RunEvent { RunId = runId, Phase = phase, Level = level, Message = message });
        await db.SaveChangesAsync(ct);
        hub.Publish(new RunNotification(runId, "event", message));
    }
}

/// <summary>
/// Hosted service that executes queued phase jobs, several at once, so many workflows can run in parallel.
/// </summary>
/// <remarks>
/// <para>
/// LIMITATIONS of running the worker inside the API process. This is fine for one user on one machine, and it is
/// the simplest thing that works, but it does not scale out or survive a busy day:
/// </para>
/// <list type="bullet">
///   <item>Agent work shares the API's CPU, memory, lifetime and credentials. A deploy or a crash stops every
///     running phase, and model keys live in the same process that serves HTTP.</item>
///   <item>Recovery is "requeue on startup" (see <see cref="RequeueInterruptedJobsAsync"/>): every run that is
///     <c>Running</c> with a <c>PendingJob</c> starts again from the beginning of its phase. There is no lease or
///     owner, so two API instances starting together would both requeue, and so both execute, the same job.</item>
///   <item>The queue (a <c>Channel</c>) and <see cref="RunEventHub"/> are in memory, so a second instance can neither
///     share the work nor stream another instance's events to its browsers.</item>
/// </list>
/// <para>
/// HOW TO IMPROVE. Make the queue a table (<c>jobs</c>: state, pool, lease_until, locked_by, attempts,
/// idempotency_key) and claim rows with <c>FOR UPDATE SKIP LOCKED</c>. Renew the lease on a heartbeat so a crashed
/// worker's job becomes claimable again, and keep phases idempotent because they can now run twice. Run the workers
/// as their own process, or pool, that scales with queue depth (and gets the credentials the API should not have).
/// Publish events with Postgres <c>LISTEN/NOTIFY</c> or Redis so every API replica can stream them.
/// </para>
/// <para>
/// The platform in <c>05-agent-platform</c> does exactly this; see its <c>docs/architecture.md</c>, section
/// "Runner, jobs and events".
/// </para>
/// </remarks>
public sealed class PhaseWorker(
    PhaseJobQueue queue,
    IServiceScopeFactory scopes,
    IOptions<StudioOptions> options,
    ILogger<PhaseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterruptedJobsAsync(stoppingToken);

        // Parallel.ForEachAsync over the channel gives bounded concurrency with no hand-written semaphore.
        await Parallel.ForEachAsync(
            queue.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrentJobs), CancellationToken = stoppingToken },
            async (job, ct) =>
            {
                // A scope per job: scoped services (DbContext, agents, prompt snapshot) live exactly as long as the job.
                await using var scope = scopes.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<PhaseJobRunner>();
                try
                {
                    await runner.ExecuteAsync(job, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Job {Kind} for run {RunId} crashed", job.Kind, job.RunId);
                }
            });
    }

    /// <summary>Runs that were mid-phase when the app stopped get their pending job queued again.</summary>
    private async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var pending = await db.Runs
            .Where(r => r.Status == RunStatus.Running && r.PendingJob != null)
            .Select(r => new { r.Id, r.PendingJob })
            .ToListAsync(ct);

        foreach (var r in pending)
            if (Enum.TryParse<JobKind>(r.PendingJob, out var kind))
                await queue.EnqueueAsync(new PhaseJob(r.Id, kind), ct);

        if (pending.Count > 0) logger.LogInformation("Re-queued {Count} interrupted job(s)", pending.Count);
    }
}
