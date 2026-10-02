using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace MrArchitectureReview.Tools;

/// <summary>
/// Read-only tools over the PR checkout that the reviewer agents can call.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a C# method becomes a tool.</b> <see cref="AIFunctionFactory.Create(Delegate, string?, string?, System.Text.Json.JsonSerializerOptions?)"/>
/// reflects over the method, turns its parameters and <see cref="DescriptionAttribute"/>s into a JSON
/// schema the model sees, and later binds the model's JSON arguments back to the parameters.
/// The descriptions are prompts: write them for the model, not for humans.
/// </para>
/// <para>
/// <b>WHY tools instead of stuffing the whole repo into the prompt?</b> Context windows are big but
/// not free: cost and latency scale with tokens, and recall drops on very long inputs. Giving the
/// agent a way to <i>pull</i> the two or three files it actually needs is cheaper and usually more
/// accurate. RAG is the other answer to the same problem (see <c>Rag/GuidelinesSearch.cs</c>): tools
/// are better for exact lookups ("open this file"), RAG for fuzzy ones ("what are our rules about X").
/// </para>
/// <para>
/// <b>Security.</b> Tool arguments come from a model that has just read untrusted PR content, so treat
/// them like user input. Every path is resolved and checked to stay inside the checkout
/// (no <c>../../etc/passwd</c>), output is size-capped, and there is deliberately no write or execute
/// tool. If you add one, put a human approval step in front of it
/// (<c>ApprovalRequiredAIFunction</c> in Microsoft.Extensions.AI wraps a tool so each call needs approval).
/// </para>
/// </remarks>
public sealed class RepositoryTools(string checkoutRoot)
{
    private const int MaxFileChars = 12_000;
    private const int MaxSearchHits = 40;

    private readonly string _root = Path.GetFullPath(checkoutRoot);

    /// <summary>The tool set handed to each reviewer agent.</summary>
    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(SearchCode, "search_code"),
    ];

    [Description("Read a file from the pull request's head commit. Use to see code around a change.")]
    public string ReadFile([Description("Repository-relative path, e.g. src/Api/OrdersController.cs")] string path)
    {
        if (!TryResolve(path, out var full) || !File.Exists(full))
            return $"ERROR: file '{path}' not found in the repository.";

        var text = File.ReadAllText(full);
        return text.Length <= MaxFileChars ? text : text[..MaxFileChars] + "\n... [truncated]";
    }

    [Description("List files under a directory of the repository (recursive, max 200 entries).")]
    public string ListFiles([Description("Repository-relative directory, or empty for the root.")] string directory = "")
    {
        if (!TryResolve(directory, out var full) || !Directory.Exists(full))
            return $"ERROR: directory '{directory}' not found.";

        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Take(200);

        return string.Join('\n', files);
    }

    [Description("Case-insensitive text search across source files. Returns path:line: text for each hit.")]
    public string SearchCode([Description("Plain text to look for, e.g. an interface or class name.")] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "ERROR: search text is empty.";

        var sb = new StringBuilder();
        var hits = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_root, file).Replace('\\', '/');
            if (rel.StartsWith(".git/", StringComparison.Ordinal) || !IsSource(rel))
                continue;

            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                if (!line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(rel).Append(':').Append(number).Append(": ").AppendLine(line.Trim());
                if (++hits >= MaxSearchHits) return sb.AppendLine("... [more hits truncated]").ToString();
            }
        }

        return hits == 0 ? $"No matches for '{text}'." : sb.ToString();
    }

    /// <summary>Resolves a model-supplied path and refuses anything outside the checkout.</summary>
    public bool TryResolve(string relative, out string fullPath)
    {
        fullPath = Path.GetFullPath(Path.Combine(_root, relative.TrimStart('/', '\\')));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return fullPath == _root || fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static bool IsSource(string path) => Path.GetExtension(path) is
        ".cs" or ".csproj" or ".json" or ".ts" or ".tsx" or ".js" or ".py" or ".java" or ".go" or ".md" or ".yml" or ".yaml" or ".sql";
}
