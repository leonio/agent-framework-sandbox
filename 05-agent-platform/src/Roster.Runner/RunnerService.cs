using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Roster.Platform.Queue;

namespace Roster.Runner;

/// <summary>Configuration section <c>Runner</c>.</summary>
public sealed class RunnerOptions
{
    /// <summary>The queue pool this runner serves (<c>general</c>; slice 2 adds <c>sandbox</c>).</summary>
    public string Pool { get; set; } = "general";

    /// <summary>How many jobs this process runs at once: one claim loop each.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>A name for this runner in the jobs table. Defaults to machine name and process id.</summary>
    public string? WorkerId { get; set; }

    /// <summary>How long a claim lasts without a heartbeat. If the runner dies, the job is claimable again after this.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>How often the lease is renewed while a job runs. Well inside <see cref="LeaseSeconds"/>.</summary>
    public int HeartbeatSeconds { get; set; } = 20;

    /// <summary>How long an idle claim loop waits before looking again.</summary>
    public int IdlePollMilliseconds { get; set; } = 1000;

    /// <summary>On shutdown, how long a running job may keep going before it is cancelled.</summary>
    public int DrainSeconds { get; set; } = 25;
}

/// <summary>
/// The runner's work: <see cref="RunnerOptions.Concurrency"/> claim loops, each taking one job at a time from the queue.
/// </summary>
/// <remarks>
/// <para>For each job a loop:</para>
/// <list type="number">
/// <item>claims it (<see cref="IJobQueue.ClaimAsync"/>), which gives this worker a lease;</item>
/// <item>starts a heartbeat that renews the lease every <see cref="RunnerOptions.HeartbeatSeconds"/>. If a renewal is
/// refused, the lease was lost (it expired and another runner took the job), so the job is cancelled here and
/// nothing about it is recorded by this worker;</item>
/// <item>runs it with <see cref="JobDispatcher"/> in a fresh DI scope, so jobs never share scoped services;</item>
/// <item>marks it complete, or failed. A failure is retried with back-off by the queue, except for jobs that can never
/// work (an unknown kind, an unreadable payload), which are failed for good straight away.</item>
/// </list>
/// <para><b>Shutdown.</b> The loops stop claiming at once. A job already running gets <see cref="RunnerOptions.DrainSeconds"/>
/// to finish (a review is usually quicker than that) before it is cancelled; a cancelled job is picked up again by
/// another runner once its lease runs out.</para>
/// <para><b>Idle.</b> An idle loop polls every second or so, with jitter so loops do not poll in step. Slice 1 load does not
/// justify more; a NOTIFY on enqueue could wake loops instantly later.</para>
/// </remarks>
public sealed class RunnerService(
    IJobQueue queue,
    IServiceScopeFactory scopes,
    IOptions<RunnerOptions> options,
    ILogger<RunnerService> logger) : BackgroundService
{
    private RunnerOptions Options => options.Value;

    private TimeSpan Lease => TimeSpan.FromSeconds(Options.LeaseSeconds);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string worker = Options.WorkerId ?? $"{Environment.MachineName}-{Environment.ProcessId}";
        int loops = Math.Max(1, Options.Concurrency);
        logger.LogInformation("Runner {Worker} serving pool {Pool} with {Loops} claim loop(s)", worker, Options.Pool, loops);

        return Task.WhenAll(Enumerable.Range(1, loops).Select(i => ClaimLoopAsync($"{worker}/{i}", stoppingToken)));
    }

    private async Task ClaimLoopAsync(string workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ClaimedJob? job;
            try
            {
                job = await queue.ClaimAsync(Options.Pool, workerId, Lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database is down or restarting. Keep the loop alive and try again shortly.
                logger.LogWarning(ex, "Worker {Worker} could not claim a job; trying again in 5 s", workerId);
                await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            if (job is null)
            {
                double jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
                await DelayAsync(TimeSpan.FromMilliseconds(Options.IdlePollMilliseconds * jitter), stoppingToken);
                continue;
            }

            await RunJobAsync(job, workerId, stoppingToken);
        }
    }

    private async Task RunJobAsync(ClaimedJob job, string workerId, CancellationToken stoppingToken)
    {
        // The job's own token: cancelled when the lease is lost, or DrainSeconds after shutdown begins.
        using var jobCancellation = new CancellationTokenSource();
        using CancellationTokenRegistration drain = stoppingToken.Register(() => jobCancellation.CancelAfter(TimeSpan.FromSeconds(Options.DrainSeconds)));

        bool leaseLost = false;
        using var stopHeartbeat = new CancellationTokenSource();
        Task heartbeat = HeartbeatAsync(job, workerId, onLeaseLost: () => { leaseLost = true; jobCancellation.Cancel(); }, stopHeartbeat.Token);

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobDispatcher>().HandleAsync(job, jobCancellation.Token);
            await queue.CompleteAsync(job.Id, workerId, CancellationToken.None);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            logger.LogWarning("Worker {Worker} lost the lease on job {JobId}; another runner has it now", workerId, job.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Worker {Worker} stopped job {JobId} for shutdown; it runs again when its lease expires", workerId, job.Id);
        }
        catch (Exception ex)
        {
            bool retry = ex is not (NotSupportedException or InvalidDataException);
            await queue.FailAsync(job.Id, workerId, ex.Message, retry, CancellationToken.None);
        }
        finally
        {
            stopHeartbeat.Cancel();
            await heartbeat;
        }
    }

    private async Task HeartbeatAsync(ClaimedJob job, string workerId, Action onLeaseLost, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Options.HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (!await queue.HeartbeatAsync(job.Id, workerId, Lease, cancellationToken))
                    {
                        onLeaseLost();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missed renewal is not yet a lost lease; there is time for the next one.
                    logger.LogWarning(ex, "Heartbeat for job {JobId} failed; trying again next tick", job.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished; stop renewing.
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
