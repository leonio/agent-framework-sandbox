using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MrArchitectureReview.Configuration;
using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Persistence;

/// <summary>
/// Persists review runs (AI findings + human decisions) as files, and reads past decisions back for the
/// feedback loop.
/// </summary>
/// <remarks>
/// <para>Layout under <c>Review:Storage:DataDirectory</c> (default <c>.review-data/</c>):</para>
/// <code>
/// reviews/{owner}-{repo}-pr{n}-{runId}.json   full ReviewRecord: the audit artefact
/// reviews/{owner}-{repo}-pr{n}-{runId}.md     the same, human-readable
/// decisions.jsonl                             one line per human decision, append-only
/// </code>
/// <para>
/// <b>WHY files and JSON Lines?</b> Zero setup, diffable, greppable, and trivially loaded into a notebook
/// or a spreadsheet to answer "which aspect gets rejected most?". Append-only JSONL is also the natural
/// shape for an evaluation dataset: each line is (finding, human label, reason). That dataset is the most
/// valuable by-product of this whole workflow. It is what you use to measure whether a prompt change
/// made the reviewers better or worse.
/// </para>
/// <para>
/// <b>Alternatives:</b> SQLite/EF Core (queries, concurrency; sample 02 in this folder uses EF with
/// LocalDB), Cosmos DB / Postgres JSONB when several people triage the same PRs, or a vector store
/// (embed each rejected finding + reason so similar future findings can be retrieved semantically rather
/// than by aspect/repository, see <c>Memory/ReviewHistoryContextProvider</c>).
/// </para>
/// </remarks>
public sealed class ReviewStore(IOptions<ReviewOptions> options)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions s_jsonLine = new(Json) { WriteIndented = false };

    // Serialise writes from concurrent runs in the same process. Cross-process safety would need a
    // file lock or a database; out of scope for a single-user CLI.
    private static readonly SemaphoreSlim s_gate = new(1, 1);

    private readonly string _root = Path.GetFullPath(options.Value.Storage.DataDirectory);

    private string ReviewsDir => Path.Combine(_root, "reviews");
    private string DecisionLog => Path.Combine(_root, "decisions.jsonl");

    public async Task<string> SaveAsync(ReviewRecord record, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ReviewsDir);
        var stem = $"{record.RepositoryKey.Replace('/', '-')}-pr{record.PullRequestNumber}-{record.RunId}";
        var jsonPath = Path.Combine(ReviewsDir, stem + ".json");

        await s_gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(record, Json), cancellationToken);
            await File.WriteAllTextAsync(Path.ChangeExtension(jsonPath, ".md"), ToMarkdown(record), cancellationToken);

            var lines = record.Findings.Select(f => JsonSerializer.Serialize(
                new DecisionLogEntry(record.RunId, record.RepositoryKey, record.PullRequestNumber, f.Finding, f.Decision), s_jsonLine));
            await File.AppendAllLinesAsync(DecisionLog, lines, cancellationToken);
        }
        finally
        {
            s_gate.Release();
        }

        return jsonPath;
    }

    /// <summary>
    /// Most recent human decisions for one repository and aspect, newest first. Rejections with a reason
    /// come first because they carry the most signal ("don't raise this again, because...").
    /// </summary>
    public async Task<IReadOnlyList<DecisionLogEntry>> GetHistoryAsync(
        string repositoryKey, ReviewAspect aspect, int max, CancellationToken cancellationToken = default)
    {
        if (max <= 0 || !File.Exists(DecisionLog))
            return [];

        List<DecisionLogEntry> entries = [];
        await foreach (var line in File.ReadLinesAsync(DecisionLog, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonSerializer.Deserialize<DecisionLogEntry>(line, s_jsonLine) is { } e
                && e.RepositoryKey == repositoryKey && e.Finding.Aspect == aspect
                && e.Decision.Verdict is not Verdict.Deferred)
            {
                entries.Add(e);
            }
        }

        return [.. entries
            .OrderByDescending(e => e.Decision.Verdict is Verdict.Rejected && e.Decision.Reason.Length > 0)
            .ThenByDescending(e => e.Decision.DecidedAt)
            .DistinctBy(e => e.Finding.Title) // the same finding re-raised on every run should count once
            .Take(max)];
    }

    internal static string ToMarkdown(ReviewRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Architecture review: {r.RepositoryKey} #{r.PullRequestNumber}")
          .AppendLine()
          .AppendLine($"**{r.PullRequestTitle}** (`{r.HeadRef}`)  ")
          .AppendLine($"Run `{r.RunId}` · {r.CompletedAt:u} · model: {r.ModelProvider}/{r.Model}  ")
          .AppendLine($"Accepted {r.AcceptedCount} · Rejected {r.RejectedCount} · Total {r.Findings.Count}")
          .AppendLine();

        foreach (var aspect in r.Aspects)
        {
            sb.AppendLine($"## {aspect.Aspect}")
              .AppendLine()
              .AppendLine($"_{aspect.Summary}_ (prompt `{aspect.PromptSha}`, {aspect.Duration.TotalSeconds:0.0}s)")
              .AppendLine();

            foreach (var (f, d) in r.Findings.Where(x => x.Finding.Aspect == aspect.Aspect).Select(x => (x.Finding, x.Decision)))
            {
                sb.AppendLine($"### {f.Id} [{f.Severity}] {f.Title}: **{d.Verdict}**")
                  .AppendLine()
                  .AppendLine(f.Detail)
                  .AppendLine()
                  .AppendLine($"- File: `{f.FilePath ?? "n/a"}` · confidence {f.Confidence:0.00}")
                  .AppendLine($"- Recommendation: {f.Recommendation}")
                  .AppendLine($"- Decision by {d.DecidedBy}: {(d.Reason.Length > 0 ? d.Reason : "(no reason given)")}")
                  .AppendLine();
            }
        }

        return sb.ToString();
    }
}

/// <summary>One line of <c>decisions.jsonl</c>.</summary>
public sealed record DecisionLogEntry(string RunId, string RepositoryKey, int PullRequestNumber, Finding Finding, TriageDecision Decision);
