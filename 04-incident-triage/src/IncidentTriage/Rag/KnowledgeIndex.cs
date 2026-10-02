using IncidentTriage.Configuration;
using IncidentTriage.Repo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Rag;

/// <summary>
/// The RAG index: two vector collections.
/// <list type="bullet">
///   <item><c>knowledge</c>: runbooks and past postmortems (Knowledge/ folder). Stable, indexed once at start-up.</item>
///   <item><c>code</c>: the repository snapshot the user pointed at. Rebuilt per run.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>WHY two collections and not one with a filter?</b> They have different lifecycles (runbooks
/// change weekly, code changes per run) and different retrieval styles: knowledge is pushed into every
/// root-cause prompt automatically (TextSearchProvider, "BeforeAIInvoke"), while code is pulled by the
/// agent on demand through a tool ("agentic RAG"), because dumping random code into every prompt is noise.</para>
///
/// <para><b>Where this would grow up in production</b></para>
/// <list type="bullet">
///   <item>A persistent, shared vector store (see <see cref="InMemoryVectorIndex"/>) so knowledge is embedded once.</item>
///   <item>Hybrid search (BM25 + vectors) helps a lot for code and error strings: exact identifiers such as
///     <c>SqlException 0x80131904</c> are lexical, not semantic.</item>
///   <item>Re-rank the top 20 with a cross-encoder or a cheap LLM call, then keep the top 4.</item>
///   <item>Index code by syntax (methods/classes via Roslyn or tree-sitter) instead of fixed line windows.</item>
///   <item>GraphRAG over services/dependencies ("checkout-api calls payments-gateway") to reason about blast radius.</item>
/// </list>
/// </remarks>
public sealed class KnowledgeIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings, RagOptions options)
{
    private readonly InMemoryVectorIndex _knowledge = new(embeddings);
    private readonly InMemoryVectorIndex _code = new(embeddings);

    // Full text of every knowledge document, for "small-to-big" retrieval (see SearchKnowledgeForProviderAsync).
    private readonly Dictionary<string, string> _documents = [];

    // C# 14 `field` keyword: a backing field without declaring one, so the setter can validate inline.
    // Before C# 14 this needed an explicit private field and a full property body.
    public int CodeChunkCount
    {
        get;
        private set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Indexes every markdown file under <paramref name="folder"/> as <paramref name="kind"/>.</summary>
    public async Task<int> IndexMarkdownFolderAsync(string folder, string kind, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return 0;

        List<KnowledgeChunk> chunks = [];
        foreach (var file in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).Order())
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var markdown = await File.ReadAllTextAsync(file, ct);
            _documents[$"{kind}:{relative}"] = markdown.Trim();
            chunks.AddRange(Chunker.Markdown(relative, kind, markdown));
        }

        await _knowledge.AddAsync(chunks, ct);
        return chunks.Count;
    }

    /// <summary>Replaces the code collection with the contents of <paramref name="snapshot"/>.</summary>
    public async Task<int> IndexRepositoryAsync(RepoSnapshot snapshot, CancellationToken ct = default)
    {
        // Per-run collection: clear so code from a previous repo never leaks into this one.
        _code.Clear();

        var extensions = options.CodeExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(snapshot.LocalPath, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsIgnored(Path.GetRelativePath(snapshot.LocalPath, f)))
            .Where(f => new FileInfo(f).Length is > 0 and < 200_000) // skip generated / minified giants
            .Order()
            .Take(options.MaxFiles);

        List<KnowledgeChunk> chunks = [];
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(snapshot.LocalPath, file).Replace('\\', '/');
            var lines = await File.ReadAllLinesAsync(file, ct);
            chunks.AddRange(Chunker.Code(relative, lines, options.CodeChunkLines, options.CodeChunkOverlap));
        }

        await _code.AddAsync(chunks, ct);
        CodeChunkCount = chunks.Count;
        return chunks.Count;
    }

    public Task<IReadOnlyList<KnowledgeHit>> SearchKnowledgeAsync(string query, int? top = null, CancellationToken ct = default)
        => _knowledge.SearchAsync(query, top ?? options.TopK, ct);

    public Task<IReadOnlyList<KnowledgeHit>> SearchCodeAsync(string query, int? top = null, CancellationToken ct = default)
        => _code.SearchAsync(query, top ?? options.TopK, ct);

    /// <summary>
    /// Adapter for Agent Framework's <see cref="TextSearchProvider"/>: it wants a
    /// <c>Func&lt;string, CancellationToken, Task&lt;IEnumerable&lt;TextSearchResult&gt;&gt;&gt;</c>.
    /// Exposing the index this way is what lets the framework do the "retrieve then inject" step for us.
    /// </summary>
    /// <remarks>
    /// <b>Small-to-big ("parent document") retrieval.</b> We <i>search</i> small chunks (precise matching: the
    /// "Symptoms" section is what resembles an incident report) but <i>return</i> the whole document the best
    /// chunks came from, because the parts the agent needs ("Likely causes", "Mitigation") rarely look like the
    /// report text. Runbooks are short, so a whole runbook is cheap. For long documents return the matched
    /// section plus its neighbours instead. Grouping by document also gives diversity: three different
    /// runbooks/postmortems instead of four chunks of the same one.
    /// </remarks>
    public async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchKnowledgeForProviderAsync(string query, CancellationToken ct)
        => (await SearchKnowledgeAsync(query, top: options.TopK * 4, ct: ct))
            .GroupBy(h => (h.Chunk.Kind, h.Chunk.Source))   // hits arrive best-first, so groups do too
            .Take(options.MaxKnowledgeDocuments)
            .Select(g => g.First())
            .Select(best => new TextSearchProvider.TextSearchResult
            {
                SourceName = best.Citation,
                SourceLink = best.Chunk.Source,
                Text = _documents.GetValueOrDefault($"{best.Chunk.Kind}:{best.Chunk.Source}", best.Chunk.Text),
            });

    private static bool IsIgnored(string relativePath)
    {
        // A real implementation would honour .gitignore (e.g. `git ls-files` when IsGit is true).
        string[] ignored = [".git", "bin", "obj", "node_modules", "dist", "build", ".vs", ".idea", "packages", "vendor"];
        return relativePath.Replace('\\', '/').Split('/').Any(segment => ignored.Contains(segment, StringComparer.OrdinalIgnoreCase))
            || relativePath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase);
    }
}
