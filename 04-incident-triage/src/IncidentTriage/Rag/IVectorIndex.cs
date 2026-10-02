namespace IncidentTriage.Rag;

/// <summary>A collection of embedded chunks that can be searched by similarity.</summary>
public interface IVectorIndex
{
    Task ClearAsync(CancellationToken ct = default);
    Task AddAsync(IReadOnlyList<KnowledgeChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int top, CancellationToken ct = default);
}
