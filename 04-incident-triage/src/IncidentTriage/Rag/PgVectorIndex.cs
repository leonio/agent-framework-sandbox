using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Npgsql;

namespace IncidentTriage.Rag;

/// <summary>Owns the Npgsql data source and the one shared <c>chunks</c> table (see docker-compose.yml).</summary>
public sealed class PgVectorStore(string connectionString) : IAsyncDisposable
{
    // 1536 matches text-embedding-3-small and the mock hashing embedder. A model with another size needs a
    // different column type, so change it here and drop the table.
    public const int Dimensions = 1536;

    public NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(connectionString);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var cmd = DataSource.CreateCommand($"""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE TABLE IF NOT EXISTS chunks (
                collection   text NOT NULL,
                id           text NOT NULL,
                kind         text NOT NULL,
                source       text NOT NULL,
                title        text NOT NULL,
                text         text NOT NULL,
                start_line   int  NOT NULL,
                content_hash text NOT NULL,
                embedding    vector({Dimensions}) NOT NULL,
                PRIMARY KEY (collection, id)
            );
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

/// <summary>
/// <see cref="IVectorIndex"/> over PostgreSQL + pgvector. Vectors are sent as text literals
/// (<c>'[0.1,0.2,...]'::vector</c>) so no extra NuGet package is needed beyond Npgsql.
/// </summary>
/// <remarks>
/// Upserts skip chunks whose content hash is unchanged, so the knowledge base is embedded once and later
/// runs cost no embedding calls. Search is an exact scan ordered by cosine distance; add an HNSW index
/// (<c>CREATE INDEX ON chunks USING hnsw (embedding vector_cosine_ops)</c>) once the table is large.
/// </remarks>
public sealed class PgVectorIndex(PgVectorStore store, string collection, IEmbeddingGenerator<string, Embedding<float>> embeddings) : IVectorIndex
{
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var cmd = store.DataSource.CreateCommand("DELETE FROM chunks WHERE collection = $1");
        cmd.Parameters.AddWithValue(collection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAsync(IReadOnlyList<KnowledgeChunk> chunks, CancellationToken ct = default)
    {
        var existing = new Dictionary<string, string>();
        await using (var cmd = store.DataSource.CreateCommand("SELECT id, content_hash FROM chunks WHERE collection = $1"))
        {
            cmd.Parameters.AddWithValue(collection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) existing[reader.GetString(0)] = reader.GetString(1);
        }

        var changed = chunks.Where(c => !existing.TryGetValue(c.Id, out var hash) || hash != Hash(c)).ToList();
        foreach (var batch in changed.Chunk(64))
        {
            var vectors = await embeddings.GenerateAsync(batch.Select(c => c.TextToEmbed), cancellationToken: ct);
            for (var i = 0; i < batch.Length; i++)
            {
                var c = batch[i];
                await using var cmd = store.DataSource.CreateCommand("""
                    INSERT INTO chunks (collection, id, kind, source, title, text, start_line, content_hash, embedding)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::vector)
                    ON CONFLICT (collection, id) DO UPDATE SET
                        kind = EXCLUDED.kind, source = EXCLUDED.source, title = EXCLUDED.title, text = EXCLUDED.text,
                        start_line = EXCLUDED.start_line, content_hash = EXCLUDED.content_hash, embedding = EXCLUDED.embedding
                    """);
                cmd.Parameters.AddWithValue(collection);
                cmd.Parameters.AddWithValue(c.Id);
                cmd.Parameters.AddWithValue(c.Kind);
                cmd.Parameters.AddWithValue(c.Source);
                cmd.Parameters.AddWithValue(c.Title);
                cmd.Parameters.AddWithValue(c.Text);
                cmd.Parameters.AddWithValue(c.StartLine);
                cmd.Parameters.AddWithValue(Hash(c));
                cmd.Parameters.AddWithValue(ToLiteral(vectors[i].Vector.Span));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int top, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var queryVector = (await embeddings.GenerateAsync([query], cancellationToken: ct))[0].Vector;
        await using var cmd = store.DataSource.CreateCommand("""
            SELECT kind, source, title, text, start_line, 1 - (embedding <=> $2::vector) AS score
            FROM chunks WHERE collection = $1
            ORDER BY embedding <=> $2::vector
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(ToLiteral(queryVector.Span));
        cmd.Parameters.AddWithValue(top);

        List<KnowledgeHit> hits = [];
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunk = new KnowledgeChunk
            {
                Kind = reader.GetString(0), Source = reader.GetString(1), Title = reader.GetString(2),
                Text = reader.GetString(3), StartLine = reader.GetInt32(4),
            };
            if (reader.GetDouble(5) > 0) hits.Add(new KnowledgeHit(chunk, reader.GetDouble(5)));
        }
        return hits;
    }

    private static string Hash(KnowledgeChunk c)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.TextToEmbed)));

    private static string ToLiteral(ReadOnlySpan<float> v)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < v.Length; i++)
            sb.Append(i == 0 ? "" : ",").Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        return sb.Append(']').ToString();
    }
}
