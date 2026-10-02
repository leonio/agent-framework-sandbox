using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace SdlcStudio.Api.Tools;

/// <summary>
/// Read-only file tools the app-analyst agent calls to explore an existing code base.
/// Exposed to the model as AI functions with <see cref="AIFunctionFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHY tools instead of pasting the whole repository into the prompt?
/// Real code bases do not fit in a context window, and most files are irrelevant. Giving the agent
/// "list" and "read" tools lets it decide what to look at, the same way a developer would.
/// </para>
/// <para>
/// RAG ALTERNATIVE (where it would really pay off): for large repositories, chunk and embed every file
/// once (Microsoft.Extensions.VectorData + an embedding IEmbeddingGenerator, stored in e.g. SQL Server
/// vector columns, Azure AI Search, Qdrant or an in-memory store) and give the agent a
/// <c>search_code(query)</c> tool, or attach a TextSearchProvider as an AIContextProvider so relevant
/// chunks are injected automatically. Semantic search finds "where is billing calculated?" in a
/// 10,000 file repo; list/read tools do not scale that far.
/// </para>
/// <para>
/// SECURITY: every path is resolved against the root and rejected if it escapes it. Never hand a model
/// an unrestricted file system tool; prompt injection inside a file could ask it to read your secrets.
/// </para>
/// </remarks>
public sealed class CodebaseTools(string rootPath)
{
    private static readonly HashSet<string> IgnoredDirectories =
        new(["bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "packages", "wwwroot"], StringComparer.OrdinalIgnoreCase);

    private const int MaxFileChars = 12_000;

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(ReadFile, "read_file"),
    ];

    [Description("Lists files under a directory of the application being analysed (recursive, max 200 entries). Returns 'path size' lines.")]
    public string ListFiles([Description("Directory relative to the repository root, '.' for the root")] string relativeDirectory = ".")
    {
        var dir = Resolve(relativeDirectory);
        if (!Directory.Exists(dir)) return $"Directory not found: {relativeDirectory}";

        var lines = EnumerateSourceFiles(dir)
            .Take(200)
            .Select(f => $"{Path.GetRelativePath(rootPath, f.FullName).Replace('\\', '/')} {f.Length}");
        return string.Join('\n', lines);
    }

    [Description("Reads a text file of the application being analysed. Large files are truncated.")]
    public string ReadFile([Description("File path relative to the repository root")] string relativePath)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return $"File not found: {relativePath}";

        var text = File.ReadAllText(path);
        return text.Length <= MaxFileChars ? text : text[..MaxFileChars] + "\n... (truncated)";
    }

    /// <summary>A deterministic inventory used to prime the analyst prompt (no model involved).</summary>
    public string BuildInventory(int max = 80)
    {
        var sb = new StringBuilder();
        foreach (var f in EnumerateSourceFiles(rootPath).Take(max))
            sb.AppendLine($"- {Path.GetRelativePath(rootPath, f.FullName).Replace('\\', '/')} ({f.Length})");
        return sb.Length == 0 ? "(no files found)" : sb.ToString();
    }

    private IEnumerable<FileInfo> EnumerateSourceFiles(string dir)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(dir, "*", options).Order())
            yield return new FileInfo(file);

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", options).Order())
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(sub))) continue;
            foreach (var f in EnumerateSourceFiles(sub))
                yield return f;
        }
    }

    private string Resolve(string relative)
    {
        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path '{relative}' is outside the application folder.");
        return full;
    }
}
