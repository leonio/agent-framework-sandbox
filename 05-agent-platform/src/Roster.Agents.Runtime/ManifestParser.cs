using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Roster.Agents.Runtime;

/// <summary>Reads an <c>AGENT.md</c> or <c>SKILL.md</c>: YAML front matter between <c>---</c> lines, then markdown.</summary>
internal static class ManifestParser
{
    private static readonly IDeserializer s_yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class ManifestDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Archetype { get; set; }
        public string? Version { get; set; }
        public string? Tier { get; set; }
        public string? Runtime { get; set; }
        public List<string>? Skills { get; set; }
        public List<string>? Capabilities { get; set; }
        public List<string>? Knowledge { get; set; }
        public string? Input { get; set; }
        public string? Output { get; set; }
        public string? OutputStrategy { get; set; }
        public string? SkillMode { get; set; }
        public string? Placement { get; set; }
    }

    private sealed class SkillDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public static (string FrontMatter, string Body) Split(string text)
    {
        text = text.ReplaceLineEndings("\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return ("", text.Trim());
        }

        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end < 0 ? ("", text.Trim()) : (text[4..end], text[(end + 4)..].Trim());
    }

    public static (AgentManifest Manifest, string Body) ParseAgent(string path)
    {
        (string front, string body) = Split(File.ReadAllText(path));
        ManifestDto dto = Deserialize<ManifestDto>(front, path) ?? throw new InvalidDataException($"{path}: empty front matter.");

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new InvalidDataException($"{path}: front matter needs a name.");
        }

        var manifest = new AgentManifest
        {
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim() ?? "",
            Archetype = dto.Archetype?.Trim() ?? "",
            Version = dto.Version?.Trim() ?? "1",
            Tier = ParseEnum(dto.Tier, ModelTier.Balanced, path, "tier"),
            Runtime = ParseEnum(dto.Runtime, RuntimeKind.Chat, path, "runtime"),
            Skills = dto.Skills ?? [],
            Capabilities = dto.Capabilities ?? [],
            Knowledge = dto.Knowledge ?? [],
            Input = NullIfEmpty(dto.Input),
            Output = NullIfEmpty(dto.Output),
            OutputStrategy = ParseEnum(dto.OutputStrategy, OutputStrategy.Native, path, "output-strategy"),
            SkillMode = ParseEnum(dto.SkillMode, SkillMode.Inline, path, "skill-mode"),
            Placement = dto.Placement is null ? null : ParseEnum(dto.Placement, Placement.InProcess, path, "placement"),
        };

        return (manifest, body);
    }

    public static ResolvedSkill ParseSkill(string path)
    {
        (string front, string body) = Split(File.ReadAllText(path));
        SkillDto dto = front.Length == 0 ? new SkillDto() : Deserialize<SkillDto>(front, path) ?? new SkillDto();
        string fallback = Path.GetFileName(Path.GetDirectoryName(path)!);
        return new ResolvedSkill(dto.Name?.Trim() ?? fallback, dto.Description?.Trim() ?? "", body);
    }

    /// <summary>
    /// Front matter is real YAML, so a value containing a colon followed by a space (a description such as
    /// "Rules: surface level") must be quoted. Say so, with the file name, instead of surfacing a parser stack.
    /// </summary>
    private static T? Deserialize<T>(string yaml, string path) where T : class
    {
        try
        {
            return s_yaml.Deserialize<T>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException(
                $"{path}: the front matter is not valid YAML ({ex.Message.Split('\n')[0]}). " +
                "If a value contains ': ' (a colon and a space), wrap the whole value in double quotes.", ex);
        }
    }

    private static T ParseEnum<T>(string? value, T fallback, string path, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        // Accept kebab-case and plain words: "on-demand" and "OnDemand" both mean OnDemand.
        return Enum.TryParse(value.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out T parsed)
            ? parsed
            : throw new InvalidDataException($"{path}: '{value}' is not a valid {field}.");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
