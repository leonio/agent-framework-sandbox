using System.Text.Json;
using CliDesignPipeline.Contracts;

namespace CliDesignPipeline.Tools;

/// <summary>
/// The folder one pipeline run writes into:
/// <code>
/// output/20261002-104512-team-task-tracker/
///   app/               &lt;- the generated application (developer + tester write here)
///   SPEC.md            &lt;- approved specification
///   REVIEW-1.md ...    &lt;- one per review round
///   MERGE_REQUEST.md   &lt;- final instructions
///   decisions.jsonl    &lt;- every human decision, with its reason
///   workflow.mmd       &lt;- Mermaid diagram of the workflow graph
/// </code>
/// </summary>
/// <remarks>
/// Keeping the AI's artefacts and the human's decisions side by side in one folder is the
/// file-system version of what sample 02 does with a database: you can always answer "why does
/// the app look like this?" by reading the run folder top to bottom.
/// </remarks>
public sealed class RunWorkspace
{
    public RunWorkspace(string outputRoot, string idea)
    {
        var slug = new string([.. idea.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')])
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Take(5);
        Root = Path.GetFullPath(Path.Combine(outputRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{string.Join('-', slug)}"));
        AppRoot = Path.Combine(Root, "app");
        Directory.CreateDirectory(AppRoot);

        // MSBuild walks *up* the folder tree for Directory.Build.props / Directory.Packages.props.
        // Runs land under this sample's folder by default, so without these two "stopper" files the
        // generated app would silently inherit our build settings (central package management,
        // warnings-as-errors) and fail to restore. Generated code should build on its own.
        File.WriteAllText(Path.Combine(Root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(Root, "Directory.Packages.props"),
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
    }

    public string Root { get; }

    public string AppRoot { get; }

    public Task WriteArtifactAsync(string fileName, string content, CancellationToken ct = default) =>
        File.WriteAllTextAsync(Path.Combine(Root, fileName), content, ct);

    public Task RecordDecisionAsync(string stage, object decision, CancellationToken ct = default) =>
        File.AppendAllTextAsync(
            Path.Combine(Root, "decisions.jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.Now, stage, decision }, PipelineJson.Compact) + Environment.NewLine,
            ct);

    /// <summary>Files currently in the app folder, relative, with forward slashes.</summary>
    public IReadOnlyList<string> ListAppFiles() =>
        [.. Directory.EnumerateFiles(AppRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(AppRoot, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Resolves a model-supplied relative path inside <see cref="AppRoot"/>, refusing anything
    /// that escapes it.
    /// </summary>
    /// <remarks>
    /// <b>Never trust a path from a model.</b> A prompt-injected or simply confused model will
    /// happily ask for <c>../../.ssh/id_rsa</c>. Normalise with <see cref="Path.GetFullPath(string)"/>
    /// and check the prefix; checking for ".." in the string is not enough (think symlinks,
    /// encoded separators, absolute paths).
    /// </remarks>
    public string ResolveInApp(string relativePath, string? requiredPrefix = null)
    {
        var full = Path.GetFullPath(Path.Combine(AppRoot, relativePath));
        var allowedRoot = Path.GetFullPath(Path.Combine(AppRoot, requiredPrefix ?? "")) ;
        if (!full.StartsWith(allowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && full != allowedRoot)
        {
            throw new UnauthorizedAccessException(
                $"Path '{relativePath}' is outside the allowed folder '{requiredPrefix ?? "."}'.");
        }

        return full;
    }
}
