using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Rag;

/// <summary>
/// A deliberately small in-memory vector index: embed with any <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>,
/// rank by cosine similarity. About forty lines, so a reader can see that "vector search" is not magic.
/// </summary>
/// <remarks>
/// <para><b>WHY not a vector database or the Microsoft.Extensions.VectorData in-memory connector?</b></para>
/// <list type="bullet">
///   <item>For a few thousand chunks, a linear scan with SIMD (<see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>)
///     takes microseconds. An approximate index (HNSW, IVF) only pays off at hundreds of thousands of vectors.</item>
///   <item>At the time of writing, <c>Microsoft.SemanticKernel.Connectors.InMemory</c> 1.74.0-preview is compiled
///     against <c>Microsoft.Extensions.VectorData.Abstractions</c> 10.1, while Agent Framework 1.23 brings in 10.10, and
///     the two are binary-incompatible (searching throws <c>TypeLoadException: VectorSearchFilter</c>). When a connector
///     release built on 10.10+ ships, swapping it in is a change to this one class.</item>
/// </list>
/// <para><b>In production</b> use a real store through Microsoft.Extensions.VectorData (Azure AI Search, Qdrant,
/// PostgreSQL + pgvector, Cosmos DB, Redis...): it persists embeddings (so you don't re-embed the corpus on every
/// start), supports metadata filters and hybrid keyword + vector search, and is shared between instances.
/// Docs: https://learn.microsoft.com/dotnet/ai/conceptual/vector-databases</para>
/// </remarks>
public sealed class InMemoryVectorIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings) : IVectorIndex
{
    private readonly List<(KnowledgeChunk Chunk, float[] Vector)> _items = [];
    private readonly Lock _gate = new();

    public int Count { get { lock (_gate) return _items.Count; } }

    public Task ClearAsync(CancellationToken ct = default) { lock (_gate) _items.Clear(); return Task.CompletedTask; }

    public async Task AddAsync(IReadOnlyList<KnowledgeChunk> chunks, CancellationToken ct = default)
    {
        // Embedding APIs cap inputs per request (OpenAI: 2048 inputs / ~300k tokens), so batch. A batch loop is
        // also the natural place for retry with back-off, progress reporting, and a content-hash cache that skips
        // chunks whose text has not changed since the last run (the biggest RAG cost saver for code).
        foreach (var batch in chunks.Chunk(64))
        {
            var vectors = await embeddings.GenerateAsync(batch.Select(c => c.TextToEmbed), cancellationToken: ct);
            lock (_gate)
            {
                for (var i = 0; i < batch.Length; i++)
                    _items.Add((batch[i], vectors[i].Vector.ToArray()));
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int top, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || Count == 0) return [];

        var queryVector = (await embeddings.GenerateAsync([query], cancellationToken: ct))[0].Vector;
        lock (_gate)
        {
            return
            [
                .. _items
                    .Select(item => new KnowledgeHit(item.Chunk, TensorPrimitives.CosineSimilarity(queryVector.Span, item.Vector)))
                    .Where(hit => hit.Score > 0)
                    .OrderByDescending(hit => hit.Score)
                    .Take(top),
            ];
        }
    }
}
