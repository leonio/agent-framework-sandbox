using System.Security.Cryptography;
using System.Text;

namespace MrArchitectureReview.Prompts;

/// <summary>
/// Loads agent instructions from the Markdown files in this folder.
/// </summary>
/// <remarks>
/// <para>
/// Each prompt is returned with a short SHA-256 of its final text. The hash is stored with every
/// review record, so months later you can answer "which version of the security prompt produced
/// this finding the team rejected?". That is the minimum viable form of prompt versioning and is
/// what makes before/after evaluation of a prompt change possible.
/// </para>
/// <para>
/// <c>{{shared-rules}}</c> is a deliberately tiny include mechanism, so the three reviewers share one
/// copy of the output and prompt-injection rules. Alternatives: a template engine (Handlebars/Liquid
/// via Semantic Kernel prompt templates), or Agent Framework <i>skills</i>
/// (<c>AgentSkillsProvider</c>), which load reusable instruction packs on demand. Skills pay off when
/// many agents share large, optional know-how; for one shared paragraph a string replace is clearer.
/// </para>
/// </remarks>
public sealed class PromptLibrary(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Prompts");

    public Prompt Load(string agentName)
    {
        var text = Read(agentName);
        if (text.Contains("{{shared-rules}}", StringComparison.Ordinal))
            text = text.Replace("{{shared-rules}}", Read("_shared-reviewer-rules"), StringComparison.Ordinal);

        return new Prompt(agentName, text, Sha(text));
    }

    private string Read(string name)
    {
        var path = Path.Combine(_directory, $"{name}.md");
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"Prompt '{name}' not found at {path}", path);
    }

    private static string Sha(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}

public sealed record Prompt(string AgentName, string Instructions, string Sha);
