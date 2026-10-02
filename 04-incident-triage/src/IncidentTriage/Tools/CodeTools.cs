using System.ComponentModel;
using System.Text;
using IncidentTriage.Rag;
using IncidentTriage.Repo;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Tools;

/// <summary>
/// Tools that let the root-cause agent look at the repository on its own ("agentic RAG").
/// </summary>
/// <remarks>
/// <para><b>Two RAG styles side by side in this sample:</b></para>
/// <list type="number">
///   <item><b>Push</b> (runbooks, postmortems): <c>TextSearchProvider</c> runs a search before every call
///     and injects the hits. Good when the knowledge is almost always relevant and small.</item>
///   <item><b>Pull</b> (code, this class): the model decides when to search and with what query. Better
///     when the corpus is large and noisy, and the right query only becomes clear mid-reasoning
///     ("the stack trace says OrderRepository... let me search for that").</item>
/// </list>
/// <para><b>WHY plain methods + <see cref="AIFunctionFactory"/>?</b> MEAI reflects over the signature and
/// <see cref="DescriptionAttribute"/>s to build the JSON schema, and binds the model's arguments back to
/// typed parameters. No hand-written schemas. Return strings, not objects, when the consumer is a model:
/// you control exactly how many tokens it sees.</para>
/// </remarks>
public sealed class CodeTools(KnowledgeIndex index, RepoSnapshot snapshot, string demoCommitsFile)
{
    public IEnumerable<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(SearchCodeAsync, "search_code"),
        AIFunctionFactory.Create(ReadFileAsync, "read_file"),
        AIFunctionFactory.Create(RecentCommitsAsync, "recent_commits"),
    ];

    [Description("Semantic search over the repository's source code. Use identifiers, exception names or log messages from the reports as the query. Returns file paths with line numbers and snippets.")]
    public async Task<string> SearchCodeAsync(
        [Description("What to look for, e.g. 'OrderRepository connection pool timeout'.")] string query,
        CancellationToken ct = default)
    {
        var hits = await index.SearchCodeAsync(query, ct: ct);
        if (hits.Count == 0) return "No code matched. Try different identifiers.";

        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            sb.AppendLine($"{h.Citation} (score {h.Score:0.00})");
            // Only the first ~25 lines: enough to recognise the code, cheap enough for 4 hits. The agent can call read_file for more.
            sb.AppendLine(string.Join('\n', h.Chunk.Text.Split('\n').Take(25)));
            sb.AppendLine("---");
        }
        return sb.ToString();
    }

    [Description("Read lines from a file in the repository, for when a search hit needs more context.")]
    public async Task<string> ReadFileAsync(
        [Description("Path relative to the repository root, exactly as returned by search_code.")] string path,
        [Description("First line to return (1-based).")] int startLine = 1,
        [Description("Last line to return (inclusive). At most 200 lines are returned.")] int endLine = 120,
        CancellationToken ct = default)
    {
        // Tools are an attack surface: the model (or a prompt-injected report) controls these arguments.
        // Resolve and confirm the path stays inside the snapshot so "../../.ssh/id_rsa" goes nowhere.
        var root = Path.GetFullPath(snapshot.LocalPath) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
            return $"File '{path}' not found in the repository.";

        var lines = await File.ReadAllLinesAsync(full, ct);
        var from = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        var to = Math.Clamp(endLine, from, Math.Min(lines.Length, from + 199));
        return string.Join('\n', Enumerable.Range(from, to - from + 1).Select(n => $"{n,4}: {lines[n - 1]}"));
    }

    [Description("List the most recent commits (short sha, date, author, subject). Deploy regressions are the most common root cause, so check this early.")]
    public async Task<string> RecentCommitsAsync(
        [Description("How many commits to return (max 30).")] int maxCount = 10,
        CancellationToken ct = default)
    {
        maxCount = Math.Clamp(maxCount, 1, 30);

        if (snapshot.IsGit)
            return await RepositoryCloner.GitAsync(snapshot.LocalPath, ct, "log", $"-n{maxCount}", "--date=short", "--pretty=format:%h %ad %an: %s");

        // The bundled demo repo is a plain folder (no .git), so its "history" lives in a text file.
        return File.Exists(demoCommitsFile)
            ? string.Join('\n', (await File.ReadAllLinesAsync(demoCommitsFile, ct)).Where(l => !l.StartsWith('#')).Take(maxCount))
            : "No git history available for this snapshot.";
    }
}
