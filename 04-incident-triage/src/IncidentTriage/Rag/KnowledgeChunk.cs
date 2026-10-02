namespace IncidentTriage.Rag;

/// <summary>
/// One retrievable piece of text: a runbook section, a past postmortem section, or a window of source code.
/// </summary>
/// <remarks>
/// <para>With a real vector database you would annotate this class for <c>Microsoft.Extensions.VectorData</c>
/// (the provider-neutral vector store abstraction) and let a connector store it:</para>
/// <code>
/// [VectorStoreKey] public required string Id { get; init; }
/// [VectorStoreData(IsIndexed = true)] public required string Kind { get; init; }
/// [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
/// public string Embedding => $"{Title}\n{Text}";   // string vector + IEmbeddingGenerator = auto-embedding
/// </code>
/// <para>See <see cref="InMemoryVectorIndex"/> for why this sample keeps its own small in-memory index instead.</para>
/// </remarks>
public sealed record KnowledgeChunk
{
    /// <summary>"runbook", "postmortem" or "code".</summary>
    public required string Kind { get; init; }

    /// <summary>Relative file path, used in the citation the agents must quote.</summary>
    public required string Source { get; init; }

    /// <summary>Heading (markdown) or "path:start-end" (code).</summary>
    public required string Title { get; init; }

    public required string Text { get; init; }

    public int StartLine { get; init; }

    public string Id => $"{Kind}:{Source}:{StartLine}";

    /// <summary>What gets embedded: title + text, so a heading like "Likely causes" pulls its section in.</summary>
    public string TextToEmbed => $"{Title}\n{Text}";
}

/// <summary>A search hit, flattened for prompts and tool results.</summary>
public sealed record KnowledgeHit(KnowledgeChunk Chunk, double Score)
{
    /// <summary>The citation format the prompts ask agents to use in their evidence lists.</summary>
    public string Citation => Chunk.Kind == "code" ? $"code:{Chunk.Source}:{Chunk.StartLine}" : $"{Chunk.Kind}:{Path.GetFileName(Chunk.Source)}";
}
