using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Roster.Agents;
using Roster.Platform.Data;
using Roster.Platform.Queue;
using Roster.Platform.Tools;

namespace Roster.Platform.Retro;

/// <summary>
/// The retro: a conversation between an assignment's owner and the <c>retro-facilitator</c> agent, ending in Good, Bad
/// and Ugly cards the person confirms (design doc section 11).
/// </summary>
/// <remarks>
/// <para><b>Turns run on runners.</b> The API stores the person's message and queues a <c>retro.turn</c> job; a runner
/// runs the facilitator over the whole stored conversation (<see cref="RunTurnAsync"/>). Nothing about a conversation
/// lives in memory between turns, so any runner can take any turn.</para>
/// <para><b>Streaming into the database.</b> The facilitator's reply is written into an in-progress message as it is
/// produced, at most every 250 ms, with a <c>retro.message</c> event each time, so the person watches it being
/// written. This rides on the Postgres event feed for now; a real pub/sub replaces it when the bus is swapped.</para>
/// <para><b>Cards.</b> The facilitator can only draft (through <c>propose_cards</c>). The person confirms, edits, discards
/// or writes cards here, and confirmed cards carry their name and title as the author, with an "assisted by" mark
/// when they began as a draft.</para>
/// </remarks>
public sealed class RetroService(
    IDbContextFactory<RosterDb> dbs,
    IJobQueue jobs,
    EventBus events,
    IServiceScopeFactory scopes,
    ILogger<RetroService> logger)
{
    /// <summary>The first message of every retro, standing in for the person opening it. The facilitator speaks first.</summary>
    public const string OpeningNote = "The person opened the retro on this assignment.";

    private const string RetroPhase = "retro";
    private static readonly TimeSpan s_writeInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Returns the assignment's retro, starting it if there is none: the session, the opening note and a queued
    /// facilitator turn, in one transaction.
    /// </summary>
    public async Task<RetroSession> OpenAsync(Guid assignmentId, string userId, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        Assignment assignment = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assignmentId && a.OwnerId == userId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Only the assignment's owner can hold its retro.");

        if (await db.RetroSessions.SingleOrDefaultAsync(s => s.AssignmentId == assignmentId, cancellationToken) is { } existing)
        {
            return existing;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var session = new RetroSession { Id = Guid.NewGuid(), AssignmentId = assignment.Id, OwnerId = userId, State = RetroState.Open, CreatedAt = now, UpdatedAt = now };
        db.RetroSessions.Add(session);
        db.RetroMessages.Add(new RetroMessage { Id = Guid.NewGuid(), SessionId = session.Id, Sequence = 0, Role = "user", Content = OpeningNote, CreatedAt = now, UpdatedAt = now });
        jobs.Add(db, JobKinds.RetroTurn, new RetroTurnPayload(session.Id));
        await db.SaveChangesAsync(cancellationToken);

        await events.PublishAsync(assignment.Id, EventKinds.RetroMessage, new { sessionId = session.Id }, cancellationToken);
        return session;
    }

    /// <summary>Adds the person's message and queues the facilitator's answer.</summary>
    public async Task<RetroMessage> PostAsync(Guid sessionId, string userId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("The message is empty.", nameof(text));
        }

        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        RetroSession session = await GetOwnedSessionAsync(db, sessionId, userId, cancellationToken);

        List<RetroMessage> messages = await db.RetroMessages.Where(m => m.SessionId == sessionId).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);
        if (messages.Count > 0 && (messages[^1].InProgress || messages[^1].Role == "user"))
        {
            throw new InvalidOperationException("The facilitator is still answering. Wait for its reply before sending another message.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var message = new RetroMessage
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Sequence = messages.Count,
            Role = "user",
            Content = text.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.RetroMessages.Add(message);
        session.UpdatedAt = now;
        jobs.Add(db, JobKinds.RetroTurn, new RetroTurnPayload(sessionId));
        await db.SaveChangesAsync(cancellationToken);

        await events.PublishAsync(session.AssignmentId, EventKinds.RetroMessage, new { sessionId, messageId = message.Id }, cancellationToken);
        return message;
    }

    /// <summary>
    /// Runs one facilitator turn: the runner's <c>retro.turn</c> handler. Safe to run twice: a turn that already has
    /// its answer does nothing, and an answer left half-written by a dead runner is started again.
    /// </summary>
    public async Task RunTurnAsync(RetroTurnPayload payload, int attempt, bool lastAttempt, CancellationToken cancellationToken)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        RetroSession session = await db.RetroSessions.AsNoTracking().SingleAsync(s => s.Id == payload.SessionId, cancellationToken);
        List<RetroMessage> messages = await db.RetroMessages.Where(m => m.SessionId == session.Id).OrderBy(m => m.Sequence).ToListAsync(cancellationToken);

        RetroMessage? reply = messages.LastOrDefault() is { Role: "assistant", InProgress: true } unfinished ? unfinished : null;
        if (reply is null && messages.LastOrDefault()?.Role == "assistant")
        {
            return; // Already answered.
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (reply is null)
        {
            reply = new RetroMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                Sequence = messages.Count,
                Role = "assistant",
                InProgress = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.RetroMessages.Add(reply);
        }

        reply.Content = "";
        await db.SaveChangesAsync(cancellationToken);

        List<ChatTurn> history = [.. messages.Where(m => m.Id != reply.Id).Select(m => new ChatTurn(m.Role, m.Content))];
        var context = new AgentRunContext(session.AssignmentId, session.OwnerId, RetroPhase, RetroPhase, attempt);
        context.Items[RetroTools.SessionIdItem] = session.Id;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IAgentRunner runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();

        // While the model streams: write the text so far into the message, at most every 250 ms, and tell the browser.
        var sinceWrite = Stopwatch.StartNew();
        async Task WritePartialAsync(string text)
        {
            if (sinceWrite.Elapsed < s_writeInterval)
            {
                return;
            }

            sinceWrite.Restart();
            await using RosterDb partialDb = await dbs.CreateDbContextAsync(cancellationToken);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            await partialDb.RetroMessages.Where(m => m.Id == reply.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, text).SetProperty(m => m.UpdatedAt, at), cancellationToken);
            await events.PublishAsync(session.AssignmentId, EventKinds.RetroMessage, new { sessionId = session.Id, messageId = reply.Id, inProgress = true }, cancellationToken);
        }

        try
        {
            ChatTurnResult result = await runner.ChatAsync(RetroTools.FacilitatorName, history, context,
                new ChatTurnOptions { OnPartial = WritePartialAsync }, cancellationToken);

            reply.Content = result.Text;
            reply.InProgress = false;
            reply.InvocationId = result.InvocationId;
        }
        catch (Exception ex) when (lastAttempt)
        {
            logger.LogWarning(ex, "The retro facilitator could not answer in session {SessionId}", session.Id);
            reply.Content = "Sorry, I couldn't answer just now. Send your message again and I'll have another go.";
            reply.InProgress = false;
            reply.InvocationId = (ex as AgentRunException)?.InvocationId;
            await SaveReplyAsync(db, reply, session, CancellationToken.None);
            throw;
        }

        await SaveReplyAsync(db, reply, session, cancellationToken);
    }

    /// <summary>
    /// Confirms a card, with the person's final wording. Also used to edit a card that is already confirmed. The agent
    /// may be changed or cleared; its hash is looked up again so the scorecard credits the right version.
    /// </summary>
    public async Task<RetroCard> ConfirmCardAsync(
        Guid cardId, string userId, CardSentiment sentiment, string text, string? agentName, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        RetroCard card = await db.RetroCards.SingleOrDefaultAsync(c => c.Id == cardId, cancellationToken)
            ?? throw new KeyNotFoundException($"No card {cardId}.");
        RetroSession session = await GetOwnedSessionAsync(db, card.SessionId, userId, cancellationToken);

        await FillCardAsync(db, card, session, userId, sentiment, text, agentName, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(session.AssignmentId, EventKinds.RetroCards, new { sessionId = session.Id, cardId }, cancellationToken);
        return card;
    }

    /// <summary>Writes a card from scratch; it is the person's own, confirmed straight away.</summary>
    public async Task<RetroCard> AddCardAsync(
        Guid sessionId, string userId, CardSentiment sentiment, string text, string? agentName, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        RetroSession session = await GetOwnedSessionAsync(db, sessionId, userId, cancellationToken);
        var card = new RetroCard { Id = Guid.NewGuid(), SessionId = session.Id, AssignmentId = session.AssignmentId, Text = "", CreatedAt = DateTimeOffset.UtcNow };
        db.RetroCards.Add(card);

        await FillCardAsync(db, card, session, userId, sentiment, text, agentName, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(session.AssignmentId, EventKinds.RetroCards, new { sessionId = session.Id, cardId = card.Id }, cancellationToken);
        return card;
    }

    /// <summary>Throws a draft away. It is kept as discarded, so the facilitator's proposals can be judged too.</summary>
    public async Task DiscardCardAsync(Guid cardId, string userId, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        RetroCard card = await db.RetroCards.SingleOrDefaultAsync(c => c.Id == cardId, cancellationToken)
            ?? throw new KeyNotFoundException($"No card {cardId}.");
        RetroSession session = await GetOwnedSessionAsync(db, card.SessionId, userId, cancellationToken);

        card.State = CardState.Discarded;
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(session.AssignmentId, EventKinds.RetroCards, new { sessionId = session.Id, cardId }, cancellationToken);
    }

    private async Task SaveReplyAsync(RosterDb db, RetroMessage reply, RetroSession session, CancellationToken cancellationToken)
    {
        reply.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(session.AssignmentId, EventKinds.RetroMessage, new { sessionId = session.Id, messageId = reply.Id, inProgress = false }, cancellationToken);
    }

    private static async Task FillCardAsync(
        RosterDb db, RetroCard card, RetroSession session, string userId, CardSentiment sentiment, string text, string? agentName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A card needs some text.", nameof(text));
        }

        // The agent must have taken part in this assignment; its latest hash there is the version the card is about.
        string? hash = agentName is null ? null : await db.Invocations.AsNoTracking()
            .Where(i => i.AssignmentId == session.AssignmentId && i.AgentName == agentName)
            .OrderByDescending(i => i.StartedAt)
            .Select(i => i.AgentHash)
            .FirstOrDefaultAsync(cancellationToken);
        if (agentName is not null && hash is null)
        {
            throw new ArgumentException($"'{agentName}' did not take part in this assignment.", nameof(agentName));
        }

        AppUser? person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        card.Sentiment = sentiment;
        card.Text = text.Trim();
        card.AgentName = agentName;
        card.AgentHash = hash;
        card.State = CardState.Confirmed;
        card.AuthorId = userId;
        card.AuthorName = person?.DisplayName;
        card.AuthorTitle = person?.Title;
        card.ConfirmedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<RetroSession> GetOwnedSessionAsync(RosterDb db, Guid sessionId, string userId, CancellationToken cancellationToken) =>
        await db.RetroSessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.OwnerId == userId, cancellationToken)
        ?? throw new UnauthorizedAccessException("This retro belongs to someone else, or does not exist.");
}
