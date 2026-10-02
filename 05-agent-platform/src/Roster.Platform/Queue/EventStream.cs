using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Roster.Platform.Data;

namespace Roster.Platform.Queue;

/// <summary>
/// The API side of live updates: listens for <see cref="EventBus"/> notifications and streams each assignment's
/// events to whoever is watching it (the API's server-sent events endpoint).
/// </summary>
/// <remarks>
/// <para><b>One connection per process.</b> As a hosted service it keeps a single Npgsql connection open with
/// <c>LISTEN roster_events</c>. EF Core cannot listen, so this is the one place that uses Npgsql directly; the
/// connection string comes from the EF context so there is one source of configuration.</para>
/// <para><b>Wake, then read.</b> A notification only says "assignment X has something new". Each subscriber then reads
/// its new rows from <c>run_events</c> with EF Core, after the last id it has seen. So nothing is lost if notifications
/// are missed: after a reconnect every subscriber is woken, and each also re-checks every 15 seconds anyway.</para>
/// <para><b>Resuming.</b> <see cref="SubscribeAsync"/> takes the last event id the browser saw (the SSE
/// <c>Last-Event-ID</c> header), so a reconnecting browser gets exactly what it missed.</para>
/// </remarks>
public sealed class EventStream(IDbContextFactory<RosterDb> dbs, ILogger<EventStream> logger) : BackgroundService
{
    private static readonly TimeSpan s_recheck = TimeSpan.FromSeconds(15);

    // assignment id → (subscriber id → that subscriber's wake-up signal)
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<bool>>> _subscribers = new();

    /// <summary>
    /// Yields the assignment's events with ids above <paramref name="afterId"/>, then keeps yielding new ones as they
    /// arrive, until <paramref name="cancellationToken"/> is cancelled (the browser went away).
    /// </summary>
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        Guid assignmentId, long afterId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Capacity one, extra writes dropped: many notifications while the subscriber is busy collapse into one wake.
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var subscriberId = Guid.NewGuid();
        _subscribers.GetOrAdd(assignmentId, _ => new())[subscriberId] = signal;

        try
        {
            long last = afterId;
            while (!cancellationToken.IsCancellationRequested)
            {
                List<RunEvent> fresh;
                await using (RosterDb db = await dbs.CreateDbContextAsync(cancellationToken))
                {
                    fresh = await db.RunEvents.AsNoTracking()
                        .Where(e => e.AssignmentId == assignmentId && e.Id > last)
                        .OrderBy(e => e.Id)
                        .Take(500)
                        .ToListAsync(cancellationToken);
                }

                foreach (RunEvent runEvent in fresh)
                {
                    last = runEvent.Id;
                    yield return runEvent;
                }

                if (fresh.Count == 500)
                {
                    continue; // A backlog: keep reading before waiting.
                }

                await WaitForWakeAsync(signal, cancellationToken);
            }
        }
        finally
        {
            if (_subscribers.TryGetValue(assignmentId, out var watchers))
            {
                watchers.TryRemove(subscriberId, out _);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string connectionString;
        await using (RosterDb db = await dbs.CreateDbContextAsync(stoppingToken))
        {
            connectionString = db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("RosterDb has no connection string; cannot listen for events.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, e) =>
                {
                    if (Guid.TryParse(e.Payload, out Guid assignmentId))
                    {
                        Wake(assignmentId);
                    }
                };

                await using (var listen = new NpgsqlCommand($"LISTEN {EventBus.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                logger.LogInformation("Listening for run events on channel {Channel}", EventBus.Channel);

                // Anything published while we were connecting was not heard; let every subscriber re-check.
                WakeAll();

                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken); // Returns after each notification batch.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Run event listener lost its connection; reconnecting in 2 s");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private void Wake(Guid assignmentId)
    {
        if (_subscribers.TryGetValue(assignmentId, out var watchers))
        {
            foreach (Channel<bool> signal in watchers.Values)
            {
                signal.Writer.TryWrite(true);
            }
        }
    }

    private void WakeAll()
    {
        foreach (Guid assignmentId in _subscribers.Keys)
        {
            Wake(assignmentId);
        }
    }

    // Waits for a notification or the periodic re-check, whichever comes first.
    private static async Task WaitForWakeAsync(Channel<bool> signal, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_recheck);
        try
        {
            await signal.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The re-check interval passed; read again.
        }
    }
}
