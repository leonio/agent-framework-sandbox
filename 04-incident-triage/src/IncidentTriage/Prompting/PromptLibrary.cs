using System.Text.RegularExpressions;

namespace IncidentTriage.Prompting;

/// <summary>An agent's identity and system prompt, loaded from <c>Prompts/&lt;name&gt;.md</c>.</summary>
public sealed record AgentPrompt(string Name, string Description, string Instructions);

/// <summary>
/// Loads prompts and shared instructions from markdown files at runtime.
/// </summary>
/// <remarks>
/// <para><b>Folder conventions</b> (all under the content root):</para>
/// <list type="bullet">
///   <item><c>Instructions/*.md</c>: house rules prepended to <i>every</i> agent (tone, citation format,
///     "never invent facts"). Change once, every agent follows.</item>
///   <item><c>Prompts/&lt;agent&gt;.md</c>: one system prompt per agent, with a tiny front-matter block
///     (<c>name</c>, <c>description</c>). The description is also the agent's <c>AIAgent.Description</c>,
///     which matters if you ever expose the agent as a tool or in a handoff workflow.</item>
///   <item><c>Skills/&lt;skill&gt;/SKILL.md</c>: NOT loaded here. Skills use the Agent Skills format and are
///     handed to <c>AgentSkillsProvider</c>, which advertises them and lets the model load one on demand.
///     Prompts are always-on; skills are pay-per-use context.</item>
/// </list>
/// <para><b>WHY files instead of C# string constants?</b> Prompt changes are the most frequent change in
/// an agentic system and are often made by people who don't write C#. Files diff nicely in PRs, can be
/// evaluated in isolation (see the prompt-evaluation-harness idea), and can later move to a database or
/// a prompt registry (Azure AI Foundry prompt assets, Langfuse, PromptLayer) behind this same class.</para>
/// <para><b>Alternative:</b> Semantic Kernel / Prompty templates with Handlebars or Liquid variables. We
/// avoid templating here because all dynamic data goes in the user message, which keeps the system
/// prompt cacheable (prompt caching only works when the prefix is byte-identical between calls).</para>
/// </remarks>
public sealed partial class PromptLibrary(string contentRoot)
{
    // HTML comments in prompt files are notes for humans (why a rule exists, who to ask). Stripped before the
    // model sees them, so authors can annotate freely without spending tokens.
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    public string ContentRoot { get; } = contentRoot;

    public string PromptsFolder => Path.Combine(ContentRoot, "Prompts");
    public string InstructionsFolder => Path.Combine(ContentRoot, "Instructions");
    public string SkillsFolder => Path.Combine(ContentRoot, "Skills");
    public string KnowledgeFolder => Path.Combine(ContentRoot, "Knowledge");
    public string SampleDataFolder => Path.Combine(ContentRoot, "SampleData");

    /// <summary>
    /// Finds the folder that holds Prompts/. While developing we prefer the project folder (walking up from
    /// bin/Debug/net10.0) so markdown edits apply on the next run without a rebuild; a published app uses
    /// the copies next to the executable.
    /// </summary>
    public static PromptLibrary Discover()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IncidentTriage.csproj")))
                return new PromptLibrary(dir.FullName);
        }
        return new PromptLibrary(AppContext.BaseDirectory);
    }

    public AgentPrompt Load(string agentName)
    {
        var path = Path.Combine(PromptsFolder, $"{agentName}.md");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No prompt file for agent '{agentName}'. Expected {path}.");

        var (frontMatter, body) = SplitFrontMatter(File.ReadAllText(path));
        var shared = LoadSharedInstructions();

        // Shared rules go FIRST: models weight the start and end of the system prompt most, and a stable
        // shared prefix across agents is friendlier to provider-side prompt caching.
        return new AgentPrompt(
            Name: frontMatter.GetValueOrDefault("name", agentName),
            Description: frontMatter.GetValueOrDefault("description", ""),
            Instructions: $"{shared}\n\n---\n\n{body.Trim()}");
    }

    private string LoadSharedInstructions() =>
        Directory.Exists(InstructionsFolder)
            ? string.Join("\n\n", Directory.EnumerateFiles(InstructionsFolder, "*.md").Order().Select(f => SplitFrontMatter(File.ReadAllText(f)).Body.Trim()))
            : "";

    /// <summary>
    /// Minimal "---\nkey: value\n---" parser. Deliberately not a YAML library: we only need flat strings,
    /// and one less dependency is one less thing to explain. Use YamlDotNet if your front-matter grows.
    /// </summary>
    private static (Dictionary<string, string> Values, string Body) SplitFrontMatter(string text)
    {
        text = text.ReplaceLineEndings("\n");
        text = HtmlComment().Replace(text, "").TrimStart();
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return ([], text);

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return ([], text);

        var values = text[4..end]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim().Trim('"'));

        return (values, text[(end + 4)..]);
    }
}
