using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Roster.Agents;
using Roster.Platform.Data;
using Roster.Platform.Queue;

namespace Roster.Platform.Tools;

/// <summary>One card as the facilitator proposes it. The shape its instructions describe and the fake endpoint sends.</summary>
public sealed record CardDraftInput(
    [property: Description("good, bad or ugly.")] string Sentiment,
    [property: Description("One or two sentences, in the person's voice.")] string Text,
    [property: Description("The agent's name if the card is about one agent, otherwise null.")] string? Agent);

/// <summary>
/// The tool behind the <c>retro.propose</c> capability: <c>propose_cards</c>, which puts draft cards in front of the
/// person. Drafts are not feedback; nothing counts until the person confirms a card (see <c>RetroService</c>).
/// </summary>
/// <remarks>
/// <para>Calling it again replaces the earlier drafts, so the facilitator can revise after the person's comments.</para>
/// <para>Cards are checked before they are stored: a known sentiment, non-empty text, at most eight cards, and an agent
/// name only if that agent took part in the assignment (otherwise it is dropped, not invented). The platform, not the
/// model, fills in the agent's version hash so scorecards credit the right version.</para>
/// <para>The retro session comes from <see cref="SessionIdItem"/> in the call's <see cref="AgentRunContext.Items"/>,
/// set by the retro service; the model never names it.</para>
/// </remarks>
internal sealed class RetroTools(IDbContextFactory<RosterDb> dbs, EventBus events, AgentRunContext context)
{
    /// <summary>The <see cref="AgentRunContext.Items"/> key holding the retro session id (a <see cref="Guid"/>).</summary>
    public const string SessionIdItem = "retro.sessionId";

    public const string FacilitatorName = "retro-facilitator";

    private const int MaxCards = 8;

    public AITool Create() => AIFunctionFactory.Create(ProposeCardsAsync, "propose_cards",
        "Hand draft Good, Bad and Ugly cards to the person to edit and confirm. Nothing is saved as feedback until they " +
        "confirm. Calling it again replaces the previous drafts.");

    private async Task<string> ProposeCardsAsync(
        [Description("The cards, three to six is plenty.")] List<CardDraftInput> cards, CancellationToken cancellationToken)
    {
        if (!context.Items.TryGetValue(SessionIdItem, out object? item) || item is not Guid sessionId)
        {
            return "Cards cannot be proposed outside a retro conversation.";
        }

        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);
        RetroSession? session = await db.RetroSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.AssignmentId == context.AssignmentId && s.OwnerId == context.UserId, cancellationToken);
        if (session is null)
        {
            return "This retro is not open for the person in this conversation.";
        }

        // The agents that actually took part, with their latest version in this assignment.
        Dictionary<string, string> hashes = await db.Invocations.AsNoTracking()
            .Where(i => i.AssignmentId == context.AssignmentId && i.AgentName != FacilitatorName)
            .GroupBy(i => i.AgentName)
            .Select(g => new { Agent = g.Key, Hash = g.OrderByDescending(i => i.StartedAt).First().AgentHash })
            .ToDictionaryAsync(x => x.Agent, x => x.Hash, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var accepted = new List<RetroCard>();
        var problems = new List<string>();
        foreach (CardDraftInput card in cards.Take(MaxCards))
        {
            if (!Enum.TryParse(card.Sentiment, ignoreCase: true, out CardSentiment sentiment))
            {
                problems.Add($"'{card.Sentiment}' is not good, bad or ugly");
                continue;
            }

            if (string.IsNullOrWhiteSpace(card.Text))
            {
                problems.Add("a card had no text");
                continue;
            }

            string? agent = card.Agent is { } name && hashes.ContainsKey(name) ? name.ToLowerInvariant() : null;
            accepted.Add(new RetroCard
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                AssignmentId = session.AssignmentId,
                Sentiment = sentiment,
                Text = card.Text.Trim(),
                AgentName = agent,
                AgentHash = agent is null ? null : hashes[agent],
                State = CardState.Draft,
                AssistedBy = FacilitatorName,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        // Replace the earlier drafts; confirmed and discarded cards stay as they are.
        await db.RetroCards.Where(c => c.SessionId == session.Id && c.State == CardState.Draft).ExecuteDeleteAsync(cancellationToken);
        db.RetroCards.AddRange(accepted);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await events.PublishAsync(session.AssignmentId, EventKinds.RetroCards, new { sessionId = session.Id, drafts = accepted.Count }, cancellationToken);

        string result = $"{accepted.Count} draft card(s) are now in the panel for the person to edit and confirm.";
        return problems.Count == 0 ? result : $"{result} Not added: {string.Join("; ", problems)}.";
    }
}
