using Microsoft.Agents.AI;

namespace MrArchitectureReview.Rag;

/// <summary>
/// Retrieval-Augmented Generation over the team's architecture guidelines (<c>Knowledge/*.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHERE RAG fits in this workflow, and why.</b> A reviewer is only as good as its idea of "good".
/// Generic models know generic best practice; they do not know that <i>your</i> team registers payment
/// gateways as keyed services, or that raw SQL is fine in <c>/migrations</c>. Pasting every guideline
/// into every prompt does not scale past a few pages, so we retrieve only the sections relevant to the
/// diff under review and inject those.
/// </para>
/// <para>
/// <b>The framework part is real; the search is deliberately naive.</b>
/// <see cref="TextSearchProvider"/> is the Agent Framework's built-in RAG context provider. You give it
/// a search delegate, and before each model call (<c>TextSearchBehavior.BeforeAIInvoke</c>) it runs the
/// search with the user's message, formats the hits with source names and asks the model to cite them.
/// Our delegate does keyword matching over the <c>Keywords:</c> line of each Markdown section, which
/// is deterministic and needs no services.
/// </para>
/// <para>
/// <b>Production replacement</b> (only the delegate changes):
/// <code>
/// // 1. Chunk + embed the guidelines once (or on change) with an IEmbeddingGenerator.
/// // 2. Upsert into a vector store collection (Microsoft.Extensions.VectorData):
/// //    InMemoryVectorStore, SqliteVectorStore, AzureAISearchVectorStore, QdrantVectorStore...
/// // 3. Search:
/// // async (query, ct) => {
/// //     var hits = collection.SearchAsync(query, top: 3, cancellationToken: ct);
/// //     return await hits.Select(h => new TextSearchProvider.TextSearchResult {
/// //         SourceName = h.Record.Title, SourceLink = h.Record.Url, Text = h.Record.Body }).ToListAsync(ct);
/// // }
/// </code>
/// Other good RAG sources for this workflow: ADRs (architecture decision records), the Confluence
/// space the publisher writes to (closing the loop), and past accepted findings for similar files.
/// </para>
/// <para>
/// <b>Alternative mode:</b> <c>TextSearchBehavior.OnDemandFunctionCalling</c> exposes search as a tool the
/// model calls when it decides it needs it. Cheaper when most reviews don't need guidelines; less
/// reliable because the model may not think to look.
/// </para>
/// </remarks>
public sealed class GuidelinesSearch
{
    private readonly IReadOnlyList<Section> _sections;

    public GuidelinesSearch(string? knowledgeDirectory = null)
    {
        var dir = knowledgeDirectory ?? Path.Combine(AppContext.BaseDirectory, "Knowledge");
        _sections = Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*.md").SelectMany(Parse)]
            : [];
    }

    /// <summary>Builds the framework's RAG context provider around our search delegate.</summary>
    public TextSearchProvider CreateProvider() => new(
        SearchAsync,
        new TextSearchProviderOptions
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            ContextPrompt = "## Team guidelines relevant to this change (retrieved)",
            CitationsPrompt = "When a finding is based on one of these guidelines, name the guideline in the finding detail.",
        });

    /// <summary>Top-3 sections whose keywords appear in the query (the PR diff + description).</summary>
    public Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        IEnumerable<TextSearchProvider.TextSearchResult> results = _sections
            .Select(s => (Section: s, Score: s.Keywords.Count(k => query.Contains(k, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => new TextSearchProvider.TextSearchResult
            {
                SourceName = $"{x.Section.File}#{x.Section.Title}",
                Text = x.Section.Body,
            })
            .ToList();

        return Task.FromResult(results);
    }

    private static IEnumerable<Section> Parse(string path)
    {
        var file = Path.GetFileName(path);
        // Sections start with "## Title", followed by a "Keywords:" line, then the body.
        foreach (var block in File.ReadAllText(path).Split("## ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries);
            var keywordLine = lines.FirstOrDefault(l => l.StartsWith("Keywords:", StringComparison.OrdinalIgnoreCase));
            string[] keywords = keywordLine is null
                ? []
                : keywordLine["Keywords:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var body = string.Join('\n', lines.Skip(1).Where(l => !l.StartsWith("Keywords:", StringComparison.OrdinalIgnoreCase)));
            yield return new Section(file, lines[0], keywords, body);
        }
    }

    private sealed record Section(string File, string Title, string[] Keywords, string Body);
}
