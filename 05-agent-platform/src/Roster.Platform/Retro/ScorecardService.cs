using Microsoft.EntityFrameworkCore;
using Roster.Platform.Data;

namespace Roster.Platform.Retro;

/// <summary>How one version of one agent has done, across every assignment.</summary>
public sealed record AgentScorecard(
    string AgentName,
    string Hash,
    string VersionLabel,
    int Calls,
    int Succeeded,
    int Repaired,
    int Failed,
    long InputTokens,
    long OutputTokens,
    double? AverageDurationMs,
    int Findings,
    int Accepted,
    int Rejected,
    int Undecided,
    double? AcceptanceRate,
    int Good,
    int Bad,
    int Ugly,
    IReadOnlyList<TitleSlice> ByTitle);

/// <summary>The same feedback split by the title of the person who gave it ("Security Engineer", "Product Owner").</summary>
public sealed record TitleSlice(string Title, int Accepted, int Rejected, int Good, int Bad, int Ugly);

/// <summary>
/// Scorecards: per agent and per version (content hash), what happened when it ran and what people thought of it.
/// </summary>
/// <remarks>
/// <para>Three sources, each grouped by agent and hash with EF Core: the ledger (calls by outcome, tokens, time), the
/// findings (accepted, rejected, undecided) and the confirmed retro cards (good, bad, ugly). Drafts and discarded
/// cards are not feedback and do not count.</para>
/// <para>Keying on the hash means an edited agent starts a fresh scorecard, so old feedback never flatters or blames a
/// version it was not about. The version label is shown for people; the hash is what counts.</para>
/// <para>Feedback is also split by the giver's title, as recorded at the time, so the platform can see that, say,
/// security engineers reject a reviewer's findings that product owners accept.</para>
/// </remarks>
public sealed class ScorecardService(IDbContextFactory<RosterDb> dbs)
{
    public async Task<IReadOnlyList<AgentScorecard>> GetAsync(string? agentName = null, CancellationToken cancellationToken = default)
    {
        await using RosterDb db = await dbs.CreateDbContextAsync(cancellationToken);

        var calls = await db.Invocations.AsNoTracking()
            .Where(i => agentName == null || i.AgentName == agentName)
            .GroupBy(i => new { i.AgentName, i.AgentHash })
            .Select(g => new
            {
                g.Key.AgentName,
                g.Key.AgentHash,
                Calls = g.Count(),
                Succeeded = g.Count(i => i.Outcome == "succeeded"),
                Repaired = g.Count(i => i.Outcome == "repaired"),
                Failed = g.Count(i => i.Outcome == "failed" || i.Outcome == "invalid-output"),
                InputTokens = g.Sum(i => i.InputTokens ?? 0),
                OutputTokens = g.Sum(i => i.OutputTokens ?? 0),
                AverageDurationMs = g.Average(i => (double?)i.DurationMs),
            })
            .ToListAsync(cancellationToken);

        var decisions = await db.Findings.AsNoTracking()
            .Where(f => agentName == null || f.AgentName == agentName)
            .GroupBy(f => new { f.AgentName, f.AgentHash, f.Decision, Title = f.DecidedByTitle })
            .Select(g => new { g.Key.AgentName, g.Key.AgentHash, g.Key.Decision, g.Key.Title, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var cards = await db.RetroCards.AsNoTracking()
            .Where(c => c.State == CardState.Confirmed && c.AgentName != null && c.AgentHash != null)
            .Where(c => agentName == null || c.AgentName == agentName)
            .GroupBy(c => new { c.AgentName, c.AgentHash, c.Sentiment, Title = c.AuthorTitle })
            .Select(g => new { AgentName = g.Key.AgentName!, AgentHash = g.Key.AgentHash!, g.Key.Sentiment, g.Key.Title, Count = g.Count() })
            .ToListAsync(cancellationToken);

        Dictionary<(string, string), string> labels = await db.AgentVersions.AsNoTracking()
            .Where(v => agentName == null || v.AgentName == agentName)
            .ToDictionaryAsync(v => (v.AgentName, v.Hash), v => v.VersionLabel, cancellationToken);

        // Every (agent, hash) that appears anywhere gets a card, even one that only has feedback so far.
        var keys = calls.Select(c => (c.AgentName, Hash: c.AgentHash))
            .Concat(decisions.Select(d => (d.AgentName, Hash: d.AgentHash)))
            .Concat(cards.Select(c => (c.AgentName, Hash: c.AgentHash)))
            .Distinct();

        var scorecards = new List<AgentScorecard>();
        foreach ((string agent, string hash) in keys)
        {
            var call = calls.FirstOrDefault(c => c.AgentName == agent && c.AgentHash == hash);
            var mine = decisions.Where(d => d.AgentName == agent && d.AgentHash == hash).ToList();
            var myCards = cards.Where(c => c.AgentName == agent && c.AgentHash == hash).ToList();

            int accepted = mine.Where(d => d.Decision == FindingDecision.Accepted).Sum(d => d.Count);
            int rejected = mine.Where(d => d.Decision == FindingDecision.Rejected).Sum(d => d.Count);

            List<TitleSlice> byTitle = [.. mine.Where(d => d.Decision != FindingDecision.Pending).Select(d => d.Title)
                .Concat(myCards.Select(c => c.Title))
                .Select(t => t ?? "(no title)")
                .Distinct()
                .Order()
                .Select(title => new TitleSlice(
                    title,
                    mine.Where(d => (d.Title ?? "(no title)") == title && d.Decision == FindingDecision.Accepted).Sum(d => d.Count),
                    mine.Where(d => (d.Title ?? "(no title)") == title && d.Decision == FindingDecision.Rejected).Sum(d => d.Count),
                    myCards.Where(c => (c.Title ?? "(no title)") == title && c.Sentiment == CardSentiment.Good).Sum(c => c.Count),
                    myCards.Where(c => (c.Title ?? "(no title)") == title && c.Sentiment == CardSentiment.Bad).Sum(c => c.Count),
                    myCards.Where(c => (c.Title ?? "(no title)") == title && c.Sentiment == CardSentiment.Ugly).Sum(c => c.Count)))];

            scorecards.Add(new AgentScorecard(
                AgentName: agent,
                Hash: hash,
                VersionLabel: labels.GetValueOrDefault((agent, hash), "?"),
                Calls: call?.Calls ?? 0,
                Succeeded: call?.Succeeded ?? 0,
                Repaired: call?.Repaired ?? 0,
                Failed: call?.Failed ?? 0,
                InputTokens: call?.InputTokens ?? 0,
                OutputTokens: call?.OutputTokens ?? 0,
                AverageDurationMs: call?.AverageDurationMs,
                Findings: mine.Sum(d => d.Count),
                Accepted: accepted,
                Rejected: rejected,
                Undecided: mine.Where(d => d.Decision == FindingDecision.Pending).Sum(d => d.Count),
                AcceptanceRate: accepted + rejected == 0 ? null : (double)accepted / (accepted + rejected),
                Good: myCards.Where(c => c.Sentiment == CardSentiment.Good).Sum(c => c.Count),
                Bad: myCards.Where(c => c.Sentiment == CardSentiment.Bad).Sum(c => c.Count),
                Ugly: myCards.Where(c => c.Sentiment == CardSentiment.Ugly).Sum(c => c.Count),
                ByTitle: byTitle));
        }

        return [.. scorecards.OrderBy(s => s.AgentName).ThenBy(s => s.Hash)];
    }
}
