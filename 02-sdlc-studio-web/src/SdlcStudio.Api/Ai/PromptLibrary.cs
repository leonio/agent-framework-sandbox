using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SdlcStudio.Api.Data;

namespace SdlcStudio.Api.Ai;

/// <summary>
/// Reads instructions, prompts and skills from the database (managed in the Admin area) and
/// renders prompt templates. Also seeds the database from the Content folder.
/// </summary>
/// <remarks>
/// WHY keep prompts in a database instead of string constants in code?
/// Prompt engineering is iterative and often done by people who do not ship C#. Admin-editable
/// prompts let you tune agent behaviour without a redeploy, and the Version column tells you which
/// prompt produced which artifact. ALTERNATIVE: prompts as embedded resources / Prompty files in the
/// repo, which gives you code review and history on prompt changes. Many teams do both: files are
/// the reviewed baseline (our Content folder) and the database holds live overrides.
/// </remarks>
public sealed partial class PromptLibrary(StudioDbContext db)
{
    // C# source-generated regexes ([GeneratedRegex]) are compiled at build time: no runtime
    // Regex construction cost and they are trimming / AOT friendly.
    [GeneratedRegex(@"\{\{\s*(?<name>[a-zA-Z0-9_]+)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^---\s*\n(?<meta>.*?)\n---\s*\n?", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    /// <summary>
    /// Builds an agent's full system prompt: its instruction text followed by every skill it lists.
    /// </summary>
    public async Task<(string Instructions, int Version)> GetAgentInstructionsAsync(string agentKey, CancellationToken ct = default)
    {
        var instruction = await db.Templates.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Key == agentKey && t.Kind == TemplateKind.Instruction, ct)
            ?? throw new InvalidOperationException($"No instruction named '{agentKey}'. Add it in the Admin area.");

        var skillKeys = instruction.Skills.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var skills = await db.Templates.AsNoTracking()
            .Where(t => t.Kind == TemplateKind.Skill && skillKeys.Contains(t.Key))
            .Select(t => t.Content)
            .ToListAsync(ct);

        // Skills are appended rather than injected through a tool or a context provider.
        // WHY: they are short and always relevant to that agent. When a skill library grows large,
        // switch to retrieval: expose skills through an AIContextProvider (or a "load_skill" tool)
        // and let the agent pull in only the ones that match the task. That is RAG over your own
        // engineering playbook, and it keeps the system prompt (and token cost) small.
        return (string.Join("\n\n", [instruction.Content, .. skills]), instruction.Version);
    }

    /// <summary>
    /// Loads every prompt template once, at the start of a phase.
    /// </summary>
    /// <remarks>
    /// WHY a snapshot instead of querying per call? Workflow executors can run in parallel (the review
    /// fan-out) and a DbContext is not thread safe. A snapshot is also consistent: every agent in one
    /// phase run sees the same prompt versions even if someone edits them in the Admin UI mid-run.
    /// </remarks>
    public async Task<PromptSnapshot> LoadPromptsAsync(CancellationToken ct = default) =>
        new(await db.Templates.AsNoTracking()
            .Where(t => t.Kind == TemplateKind.Prompt)
            .ToDictionaryAsync(t => t.Key, t => t.Content, ct));

    /// <remarks>
    /// A deliberately tiny renderer. ALTERNATIVES: Handlebars.Net / Fluid (Liquid) when you need
    /// loops and conditionals, or Semantic Kernel's prompt template engines.
    /// </remarks>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        Placeholder().Replace(template, m =>
            values.TryGetValue(m.Groups["name"].Value, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "(none)");

    /// <summary>Loads Content/{instructions,prompts,skills}/*.md into the database.</summary>
    /// <param name="overwrite">True = reset to defaults (Admin button). False = only add missing keys.</param>
    public async Task<int> SeedAsync(string contentRoot, bool overwrite, CancellationToken ct = default)
    {
        (string Folder, TemplateKind Kind)[] sources =
            [("instructions", TemplateKind.Instruction), ("prompts", TemplateKind.Prompt), ("skills", TemplateKind.Skill)];

        var existing = await db.Templates.ToDictionaryAsync(t => t.Key, ct);
        var changed = 0;

        foreach (var (folder, kind) in sources)
        {
            var dir = Path.Combine(contentRoot, folder);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.md").Order())
            {
                var key = Path.GetFileNameWithoutExtension(file);
                var (meta, body) = ParseFrontMatter(await File.ReadAllTextAsync(file, ct));

                if (existing.TryGetValue(key, out var template))
                {
                    if (!overwrite) continue;
                    template.Version++;
                }
                else
                {
                    template = new PromptTemplate { Key = key, Title = key, Content = body };
                    db.Templates.Add(template);
                }

                template.Kind = kind;
                template.Title = meta.GetValueOrDefault("title", key);
                template.Description = meta.GetValueOrDefault("description", "");
                template.Skills = meta.GetValueOrDefault("skills", "");
                template.Content = body;
                template.UpdatedAt = DateTimeOffset.UtcNow;
                changed++;
            }
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Minimal "key: value" front matter parser. ALTERNATIVE: YamlDotNet for real YAML.</summary>
    internal static (Dictionary<string, string> Meta, string Body) ParseFrontMatter(string text)
    {
        text = text.ReplaceLineEndings("\n");
        var match = FrontMatter().Match(text);
        if (!match.Success) return ([], text.Trim());

        var meta = match.Groups["meta"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        return (meta, text[match.Length..].Trim());
    }
}

/// <summary>Immutable set of prompt templates for one phase run.</summary>
public sealed class PromptSnapshot(IReadOnlyDictionary<string, string> templates)
{
    public string Render(string promptKey, IReadOnlyDictionary<string, string?> values) =>
        templates.TryGetValue(promptKey, out var template)
            ? PromptLibrary.Render(template, values)
            : throw new InvalidOperationException($"No prompt named '{promptKey}'. Add it in the Admin area.");
}
