using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Roster.Api.Auth;
using Roster.Platform.Data;
using Roster.Platform.Retro;

namespace Roster.Api.Endpoints;

/// <summary>
/// The retro tab: open the conversation with the facilitator, talk to it, and turn its draft cards into confirmed
/// Good, Bad and Ugly feedback.
/// </summary>
/// <remarks>
/// <para>A retro belongs to the assignment's owner alone: the facilitator reads their findings, decisions and the
/// agents' reasoning, so admins do not get to read it.</para>
/// <para>The facilitator answers on a runner. Posting a message returns at once; its reply arrives as
/// <c>retro.message</c> events on the assignment's event stream, growing while it is written. Refetch the retro on
/// each one.</para>
/// </remarks>
public static class RetroEndpoints
{
    public sealed record MessageView(Guid Id, int Sequence, string Role, string Content, bool InProgress, DateTimeOffset CreatedAt);

    public sealed record CardView(
        Guid Id, CardSentiment Sentiment, string Text, string? Agent, CardState State, string? AssistedBy,
        string? AuthorName, string? AuthorTitle, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt);

    public sealed record RetroView(Guid SessionId, Guid AssignmentId, RetroState State, IReadOnlyList<MessageView> Messages, IReadOnlyList<CardView> Cards);

    public sealed record MessageInput(string Text);

    public sealed record CardInput(CardSentiment Sentiment, string Text, string? Agent);

    public static IEndpointRouteBuilder MapRetroEndpoints(this IEndpointRouteBuilder app)
    {
        // The retro of an assignment, if one has been opened.
        app.MapGet("/api/assignments/{id:guid}/retro", async (Guid id, ClaimsPrincipal user, RosterDb db, CancellationToken cancellationToken) =>
            await db.RetroSessions.AsNoTracking().SingleOrDefaultAsync(s => s.AssignmentId == id && s.OwnerId == user.UserId(), cancellationToken) is { } session
                ? Results.Ok(await ViewAsync(db, session, cancellationToken))
                : Results.NotFound())
            .WithTags("Retro");

        // Opens the retro (the facilitator speaks first) or returns the one already open.
        app.MapPost("/api/assignments/{id:guid}/retro", async (Guid id, ClaimsPrincipal user, RosterDb db, RetroService retro, CancellationToken cancellationToken) =>
            await ViewAsync(db, await retro.OpenAsync(id, user.UserId(), cancellationToken), cancellationToken))
            .WithTags("Retro");

        RouteGroupBuilder retros = app.MapGroup("/api/retros/{sessionId:guid}").WithTags("Retro");

        retros.MapPost("/messages", async (Guid sessionId, MessageInput input, ClaimsPrincipal user, RetroService retro, CancellationToken cancellationToken) =>
        {
            RetroMessage message = await retro.PostAsync(sessionId, user.UserId(), input.Text, cancellationToken);
            return TypedResults.Accepted((string?)null, ToView(message));
        });

        // A card the person writes themselves: theirs from the start, confirmed straight away.
        retros.MapPost("/cards", async (Guid sessionId, CardInput input, ClaimsPrincipal user, RetroService retro, CancellationToken cancellationToken) =>
            ToView(await retro.AddCardAsync(sessionId, user.UserId(), input.Sentiment, input.Text, input.Agent, cancellationToken)));

        RouteGroupBuilder cards = app.MapGroup("/api/retro-cards/{id:guid}").WithTags("Retro");

        // Confirm a draft with the person's final wording, or edit a confirmed card.
        cards.MapPost("/confirm", async (Guid id, CardInput input, ClaimsPrincipal user, RetroService retro, CancellationToken cancellationToken) =>
            ToView(await retro.ConfirmCardAsync(id, user.UserId(), input.Sentiment, input.Text, input.Agent, cancellationToken)));

        cards.MapPost("/discard", async (Guid id, ClaimsPrincipal user, RetroService retro, CancellationToken cancellationToken) =>
        {
            await retro.DiscardCardAsync(id, user.UserId(), cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<RetroView> ViewAsync(RosterDb db, RetroSession session, CancellationToken cancellationToken)
    {
        List<RetroMessage> messages = await db.RetroMessages.AsNoTracking().Where(m => m.SessionId == session.Id).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);
        List<RetroCard> cards = await db.RetroCards.AsNoTracking().Where(c => c.SessionId == session.Id).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
        return new RetroView(session.Id, session.AssignmentId, session.State, [.. messages.Select(ToView)], [.. cards.Select(ToView)]);
    }

    private static MessageView ToView(RetroMessage m) => new(m.Id, m.Sequence, m.Role, m.Content, m.InProgress, m.CreatedAt);

    private static CardView ToView(RetroCard c) =>
        new(c.Id, c.Sentiment, c.Text, c.AgentName, c.State, c.AssistedBy, c.AuthorName, c.AuthorTitle, c.CreatedAt, c.ConfirmedAt);
}
