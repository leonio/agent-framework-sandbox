using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace IncidentTriage.Ai.Mock;

/// <summary>
/// An offline <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> based on the "hashing trick".
/// </summary>
/// <remarks>
/// <para><b>WHY ship a fake embedder?</b> So the RAG pipeline (chunk, embed, upsert, vector search,
/// inject) is <i>real</i> code that runs end to end without an API key. Retrieval quality is that of a
/// bag-of-words search, which for runbooks and error strings is honestly not bad; real embeddings add
/// synonyms and paraphrase ("DB pool exhausted" ≈ "no free connections").</para>
///
/// <para>How it works: tokens (with camelCase / snake_case split, so <c>GetConnectionAsync</c> matches
/// "connection") are hashed with a <i>stable</i> FNV-1a hash into 1536 buckets with a ±1 sign, then the
/// vector is L2-normalised so cosine similarity behaves. Never use <c>string.GetHashCode()</c> for this:
/// it is randomised per process, so vectors would not match across runs.</para>
///
/// <para>Because it implements the Microsoft.Extensions.AI interface, nothing else in the program knows
/// or cares that it is fake. That is the whole point of the abstraction.</para>
/// </remarks>
public sealed partial class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Same size as text-embedding-3-small, purely so logs and docs look familiar. Any size works.</summary>
    public const int Dimensions = 1536;

    private static readonly HashSet<string> StopWords =
        ["the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were", "be", "with", "at", "by", "it", "this", "that", "from", "as", "we", "our"];

    [GeneratedRegex(@"[A-Za-z][a-z]+|[A-Z]+(?![a-z])|\d{3,}")]
    private static partial Regex Tokens();

    public EmbeddingGeneratorMetadata Metadata { get; } = new("hashing-mock", defaultModelId: "hashing-1536", defaultModelDimensions: Dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v)))));

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (Match m in Tokens().Matches(text))
        {
            var token = Stem(m.Value.ToLowerInvariant());
            if (token.Length < 3 || StopWords.Contains(token)) continue;

            var hash = Fnv1a(token);
            vector[hash % (uint)vector.Length] += (hash & 0x8000_0000) == 0 ? 1f : -1f;
        }

        // L2-normalise using SIMD-accelerated TensorPrimitives (System.Numerics.Tensors).
        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0) TensorPrimitives.Divide(vector, norm, vector);
        return vector;
    }

    /// <summary>Crude plural/verb stemming so "timeouts" ≈ "timeout" and "exhausted" ≈ "exhaust".</summary>
    private static string Stem(string t) => t switch
    {
        _ when t.EndsWith("ies", StringComparison.Ordinal) && t.Length > 4 => t[..^3] + "y",
        _ when t.EndsWith("ing", StringComparison.Ordinal) && t.Length > 5 => t[..^3],
        _ when t.EndsWith("ed", StringComparison.Ordinal) && t.Length > 4 => t[..^2],
        _ when t.EndsWith('s') && !t.EndsWith("ss", StringComparison.Ordinal) && t.Length > 3 => t[..^1],
        _ => t,
    };

    private static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var c in s) hash = (hash ^ c) * 16777619u;
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
