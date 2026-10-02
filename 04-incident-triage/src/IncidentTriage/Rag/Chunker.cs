using System.Text;

namespace IncidentTriage.Rag;

/// <summary>
/// Turns files into <see cref="KnowledgeChunk"/>s.
/// </summary>
/// <remarks>
/// Chunking is the single biggest lever on RAG quality, and the right answer depends on the content:
/// <list type="bullet">
///   <item><b>Runbooks / postmortems</b> are written by humans in sections, so we split on markdown
///     headings: each "## Symptoms", "## Mitigation" block is self-contained and has a meaningful title.</item>
///   <item><b>Code</b> is split into fixed line windows with overlap. Crude but language-agnostic; the
///     overlap stops a method being cut in half with neither half matching. Better: split by syntax
///     (Roslyn for C#, tree-sitter for everything) so each chunk is one member with its signature.</item>
/// </list>
/// Alternatives: token-based splitters (Microsoft.ML.Tokenizers + a fixed token budget), semantic
/// chunking (split where embedding similarity between sentences drops), or "late chunking".
/// </remarks>
internal static class Chunker
{
    public static IEnumerable<KnowledgeChunk> Markdown(string relativePath, string kind, string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var docTitle = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? relativePath;

        var heading = docTitle;
        var start = 1;
        var buffer = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                // Keep the preamble ("# Title", owner, scope) attached to the first section instead of emitting
                // a near-empty chunk that would win searches on title words alone and carry no content.
                var preambleOnly = buffer.ToString().Split('\n').All(l => l.Trim().Length == 0 || l.StartsWith('#'));
                if (buffer.Length > 0 && !preambleOnly) yield return Make(heading, start, buffer);
                heading = $"{docTitle} / {line[3..].Trim()}";
                if (!preambleOnly) { start = i + 1; buffer.Clear(); }
            }
            buffer.AppendLine(line);
        }
        if (buffer.Length > 0) yield return Make(heading, start, buffer);

        // NOTE: a chunk that is only the "# Title" line still gets emitted for documents without "##" sections;
        // for sectioned documents the title line is folded into the first section (see the loop above).
        KnowledgeChunk Make(string title, int startLine, StringBuilder text) => new()
        {
            Kind = kind,
            Source = relativePath,
            Title = title,
            StartLine = startLine,
            Text = text.ToString().Trim(),
        };
    }

    public static IEnumerable<KnowledgeChunk> Code(string relativePath, string[] lines, int window, int overlap)
    {
        var step = Math.Max(1, window - overlap);
        for (var start = 0; start < lines.Length; start += step)
        {
            var end = Math.Min(lines.Length, start + window);
            // Prefix every line with its number so the model can cite "code:path:line" precisely.
            var text = string.Join('\n', lines[start..end].Select((l, i) => $"{start + i + 1,4}: {l}"));
            yield return new KnowledgeChunk
            {
                Kind = "code",
                Source = relativePath,
                Title = $"{relativePath}:{start + 1}-{end}",
                StartLine = start + 1,
                Text = text,
            };
            if (end == lines.Length) yield break;
        }
    }
}
