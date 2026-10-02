using System.Collections.Frozen;
using System.Text;

namespace CliDesignPipeline.Content;

/// <summary>A parsed <c>prompts/*.md</c> file.</summary>
/// <param name="Agent">Agent name from front matter; also the key used for per-agent model overrides.</param>
/// <param name="Description">Shown in the console and used as the agent's Description.</param>
/// <param name="Body">The system prompt itself (Markdown, may contain <c>{{placeholders}}</c>).</param>
/// <param name="Skills">Names of folders under <c>skills/</c> to append to the instructions.</param>
/// <param name="PinnedInstructions">Names of files under <c>instructions/</c> always included in full.</param>
/// <param name="Temperature">Sampling temperature for this agent.</param>
public sealed record PromptDefinition(
    string Agent,
    string Description,
    string Body,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> PinnedInstructions,
    float? Temperature);

/// <summary>A <c>skills/&lt;name&gt;/SKILL.md</c> file.</summary>
public sealed record SkillDefinition(string Name, string Description, string Body);

/// <summary>An <c>instructions/*.md</c> file split into retrievable sections.</summary>
public sealed record InstructionDocument(string Name, string Title, IReadOnlyList<InstructionSection> Sections);

public sealed record InstructionSection(string Document, string Heading, string Text);

/// <summary>
/// Loads the three kinds of authored content this sample keeps <b>outside</b> of C#:
/// </summary>
/// <remarks>
/// <list type="table">
///   <item><term>prompts/</term><description>
///     One per agent. Persona, task, rules and output contract. "Who am I and what do I return?"
///   </description></item>
///   <item><term>skills/</term><description>
///     Reusable procedures (a checklist, an interview technique, a git convention) that any agent
///     can opt into via <c>skills: [...]</c>. Same <c>SKILL.md</c> + front-matter layout used by
///     Claude/Copilot agent skills, so they are portable. "How do I do this kind of task?"
///   </description></item>
///   <item><term>instructions/</term><description>
///     Standing organisational rules (coding standards, definition of done). An agent can
///     <i>pin</i> a whole document (always in context) or let the retrieval provider
///     (<see cref="Rag.InstructionsContextProvider"/>) pull just the relevant sections per call.
///     "What rules apply here?"
///   </description></item>
/// </list>
/// <para>
/// <b>Why files and not C# string constants?</b> The people who should tune an interviewer's
/// tone or a review checklist are rarely the people who compile the app. Files diff well in
/// code review, can be owned via CODEOWNERS, and sample 02 shows the next step: the same
/// shapes stored in a database and edited from an admin UI.
/// </para>
/// <para>
/// <b>Why a hand-rolled front-matter parser?</b> We only need <c>key: value</c> and
/// <c>key: [a, b]</c>. A real YAML parser (YamlDotNet) is the right call as soon as you need
/// nesting or multi-line values; the dependency was not worth it for a sample.
/// Alternative: Microsoft's <c>.prompty</c> format, or Semantic Kernel prompt templates with
/// Handlebars/Liquid if you need conditionals and loops inside prompts.
/// </para>
/// </remarks>
public sealed class PromptLibrary
{
    private readonly FrozenDictionary<string, PromptDefinition> _prompts;
    private readonly FrozenDictionary<string, SkillDefinition> _skills;

    public PromptLibrary(string contentRoot)
    {
        _prompts = Directory.EnumerateFiles(Path.Combine(contentRoot, "prompts"), "*.md")
            .Select(ParsePrompt)
            .ToFrozenDictionary(p => p.Agent, StringComparer.OrdinalIgnoreCase);

        _skills = Directory.EnumerateDirectories(Path.Combine(contentRoot, "skills"))
            .Select(dir => Path.Combine(dir, "SKILL.md"))
            .Where(File.Exists)
            .Select(ParseSkill)
            .ToFrozenDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        Instructions = Directory.EnumerateFiles(Path.Combine(contentRoot, "instructions"), "*.md")
            .Select(ParseInstruction)
            .ToArray();
    }

    /// <summary>All instruction documents, already split by <c>##</c> heading for retrieval.</summary>
    public IReadOnlyList<InstructionDocument> Instructions { get; }

    public PromptDefinition GetPrompt(string agent) =>
        _prompts.TryGetValue(agent, out var prompt)
            ? prompt
            : throw new InvalidOperationException($"No prompt found for agent '{agent}'. Expected prompts/{agent}.md.");

    /// <summary>
    /// Builds the final system instructions for an agent: prompt body (with placeholders filled)
    /// + each referenced skill + each pinned instruction document.
    /// </summary>
    /// <remarks>
    /// Composition order matters: models weight the start and end of a system prompt most, so the
    /// persona and output contract go first and the long reference material last.
    /// </remarks>
    public string ComposeInstructions(PromptDefinition prompt, IReadOnlyDictionary<string, string>? variables = null)
    {
        var body = prompt.Body;
        foreach (var (key, value) in variables ?? FrozenDictionary<string, string>.Empty)
        {
            body = body.Replace($"{{{{{key}}}}}", value, StringComparison.Ordinal);
        }

        var sb = new StringBuilder(body.Trim()).AppendLine().AppendLine();

        foreach (var skillName in prompt.Skills)
        {
            var skill = _skills.GetValueOrDefault(skillName)
                ?? throw new InvalidOperationException($"Prompt '{prompt.Agent}' references unknown skill '{skillName}'.");
            sb.AppendLine($"# Skill: {skill.Name}").AppendLine().AppendLine(skill.Body.Trim()).AppendLine();
        }

        foreach (var name in prompt.PinnedInstructions)
        {
            var doc = Instructions.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Prompt '{prompt.Agent}' pins unknown instructions '{name}'.");
            sb.AppendLine($"# Standing instructions: {doc.Title}").AppendLine();
            foreach (var section in doc.Sections)
            {
                sb.AppendLine($"## {section.Heading}").AppendLine(section.Text).AppendLine();
            }
        }

        return sb.ToString();
    }

    private static PromptDefinition ParsePrompt(string path)
    {
        var (meta, body) = SplitFrontMatter(File.ReadAllText(path));
        return new PromptDefinition(
            Agent: meta.GetValueOrDefault("agent") ?? Path.GetFileNameWithoutExtension(path),
            Description: meta.GetValueOrDefault("description") ?? "",
            Body: body,
            Skills: ParseList(meta.GetValueOrDefault("skills")),
            PinnedInstructions: ParseList(meta.GetValueOrDefault("instructions")),
            Temperature: float.TryParse(meta.GetValueOrDefault("temperature"), System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null);
    }

    private static SkillDefinition ParseSkill(string path)
    {
        var (meta, body) = SplitFrontMatter(File.ReadAllText(path));
        return new SkillDefinition(
            meta.GetValueOrDefault("name") ?? Path.GetFileName(Path.GetDirectoryName(path)!),
            meta.GetValueOrDefault("description") ?? "",
            body);
    }

    private static InstructionDocument ParseInstruction(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var lines = File.ReadAllLines(path);
        var title = lines.FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? name;

        // Chunk by "## " heading. For RAG, *how you chunk* usually matters more than which vector
        // database you pick: a heading-sized chunk is self-contained and citeable. Fixed-size
        // token windows with overlap are the fallback when documents have no structure.
        List<InstructionSection> sections = [];
        string? heading = null;
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                heading = line[3..].Trim();
            }
            else if (heading is not null)
            {
                text.AppendLine(line);
            }
        }

        Flush();
        return new InstructionDocument(name, title, sections);

        void Flush()
        {
            if (heading is not null)
            {
                sections.Add(new InstructionSection(name, heading, text.ToString().Trim()));
            }

            text.Clear();
        }
    }

    private static (Dictionary<string, string> Meta, string Body) SplitFrontMatter(string content)
    {
        content = content.ReplaceLineEndings("\n");
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return ([], content);
        }

        var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return ([], content);
        }

        var meta = content[4..end]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts is [_, _])
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        return (meta, content[(end + 4)..].TrimStart('\n'));
    }

    private static string[] ParseList(string? value) =>
        value?.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
