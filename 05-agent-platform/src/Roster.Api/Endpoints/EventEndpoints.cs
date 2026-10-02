using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Roster.Platform.Data;
using Roster.Platform.Queue;

namespace Roster.Api.Endpoints;

/// <summary>
/// Live updates for one assignment as server-sent events. The web app opens an <c>EventSource</c> on this URL and,
/// on each event, refetches what changed ("something changed, refetch", as in sample 02).
/// </summary>
/// <remarks>
/// <para>Each event carries the run event's id as the SSE <c>id</c>, its kind as the SSE <c>event</c> and its small
/// JSON payload as <c>data</c>. When the connection drops, the browser reconnects by itself and sends the last id it
/// saw in <c>Last-Event-ID</c>, and the stream resumes exactly after it. <c>?after=</c> does the same by hand.</para>
/// <para>The cookie authenticates the stream, which is why sign-in is cookie-based (an <c>EventSource</c> cannot send
/// an Authorization header).</para>
/// </remarks>
public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assignments/{id:guid}/events", async (Guid id, long? after, HttpRequest request, ClaimsPrincipal user,
            RosterDb db, EventStream stream, CancellationToken cancellationToken) =>
        {
            await AssignmentEndpoints.ReadableAsync(db, id, user, cancellationToken);

            long resumeAfter = long.TryParse(request.Headers["Last-Event-ID"], out long lastSeen) ? lastSeen : after ?? 0;
            return TypedResults.ServerSentEvents(Items(stream.SubscribeAsync(id, resumeAfter, cancellationToken), cancellationToken));
        })
        .WithTags("Assignments");

        return app;
    }

    private static async IAsyncEnumerable<SseItem<string>> Items(IAsyncEnumerable<RunEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (RunEvent runEvent in events.WithCancellation(cancellationToken))
        {
            yield return new SseItem<string>(runEvent.PayloadJson, runEvent.Kind) { EventId = runEvent.Id.ToString() };
        }
    }
}
