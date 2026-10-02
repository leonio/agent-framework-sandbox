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
