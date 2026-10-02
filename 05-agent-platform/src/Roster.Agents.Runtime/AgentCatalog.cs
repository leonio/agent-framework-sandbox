using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Roster.Agents.Runtime;

public sealed class AgentCatalogOptions
{
    /// <summary>Folder that holds <c>agents/</c> and <c>skills/</c>. Defaults to the library's content next to the host.</summary>
    public string ContentRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "agent-library");

    /// <summary>Assemblies scanned for <see cref="AgentContractAttribute"/> types.</summary>
    public List<Assembly> ContractAssemblies { get; } = [];
}

/// <summary>
/// Loads every <c>AGENT.md</c> under the content root into an <see cref="AgentDefinition"/>: parses the manifest, resolves
/// skills and contracts, composes the instructions and computes the content hash that identifies this exact behaviour.
/// </summary>
/// <remarks>
/// The hash covers the instructions, the resolved skills in order, the capabilities, tier, runtime, output strategy and
/// the output schema. It deliberately leaves out the description and the version label: those can change without the
/// agent behaving differently, and scorecards must not split because someone fixed a typo in a description.
/// </remarks>
public sealed class AgentCatalog : IAgentCatalog
{
    private readonly Dictionary<string, AgentDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    public AgentCatalog(IOptions<AgentCatalogOptions> options, ILogger<AgentCatalog> logger)
    {
        AgentCatalogOptions o = options.Value;
        string agentsRoot = Path.Combine(o.ContentRoot, "agents");
        string skillsRoot = Path.Combine(o.ContentRoot, "skills");

        if (!Directory.Exists(agentsRoot))
        {
            throw new DirectoryNotFoundException($"No agent library at '{agentsRoot}'. Is the library project referenced by this host?");
        }

        Dictionary<string, Type> contracts = o.ContractAssemblies
            .SelectMany(a => a.GetTypes())
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<AgentContractAttribute>()))
            .Where(x => x.Attr is not null)
            .ToDictionary(x => x.Attr!.Name, x => x.Type, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, ResolvedSkill> sharedSkills = LoadSkills(skillsRoot);

        foreach (string file in Directory.EnumerateFiles(agentsRoot, "AGENT.md", SearchOption.AllDirectories).Order())
        {
            string dir = Path.GetDirectoryName(file)!;
            (AgentManifest manifest, string body) = ManifestParser.ParseAgent(file);

            Dictionary<string, ResolvedSkill> privateSkills = LoadSkills(Path.Combine(dir, "skills"));
            List<ResolvedSkill> skills = [];
            foreach (string skillName in manifest.Skills)
            {
                if (privateSkills.TryGetValue(skillName, out ResolvedSkill? s) || sharedSkills.TryGetValue(skillName, out s))
                {
                    skills.Add(s);
                }
                else
                {
                    throw new InvalidDataException($"{file}: unknown skill '{skillName}'.");
                }
            }

            foreach (string capability in manifest.Capabilities.Where(c => !CapabilityCatalog.All.ContainsKey(c)))
            {
                logger.LogWarning("Agent {Agent} names unknown capability '{Capability}'; it is treated as the highest risk class.", manifest.Name, capability);
            }

            Type? inputType = Resolve(contracts, manifest.Input, file);
            Type? outputType = Resolve(contracts, manifest.Output, file);

            var definition = new AgentDefinition(manifest, body, skills, inputType, outputType, ComputeHash(manifest, body, skills, outputType));
            if (!_byName.TryAdd(manifest.Name, definition))
            {
                throw new InvalidDataException($"{file}: duplicate agent name '{manifest.Name}'.");
            }

            logger.LogInformation("Loaded agent {Agent} ({Hash}), {Skills} skill(s), capabilities [{Capabilities}]",
                manifest.Name, definition.ShortHash, skills.Count, string.Join(", ", manifest.Capabilities));
        }
    }

    public IReadOnlyList<AgentDefinition> All => [.. _byName.Values.OrderBy(a => a.Manifest.Archetype).ThenBy(a => a.Name)];

    public AgentDefinition Get(string name) =>
        _byName.TryGetValue(name, out AgentDefinition? definition)
            ? definition
            : throw new KeyNotFoundException($"No agent named '{name}' in the library.");

    private static Dictionary<string, ResolvedSkill> LoadSkills(string root) =>
        !Directory.Exists(root)
            ? new(StringComparer.OrdinalIgnoreCase)
            : Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories)
                .Select(ManifestParser.ParseSkill)
                .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    private static Type? Resolve(Dictionary<string, Type> contracts, string? name, string file)
    {
        if (name is null)
        {
            return null;
        }

        return contracts.TryGetValue(name, out Type? type)
            ? type
            : throw new InvalidDataException($"{file}: unknown contract '{name}'. Is its assembly registered with the catalog?");
    }

    private static string ComputeHash(AgentManifest m, string instructions, IReadOnlyList<ResolvedSkill> skills, Type? outputType)
    {
        var sb = new StringBuilder()
            .Append(m.Name).Append('\n')
            .Append(m.Tier).Append('\n')
            .Append(m.Runtime).Append('\n')
            .Append(m.OutputStrategy).Append('\n')
            .Append(m.SkillMode).Append('\n')
            .Append(string.Join(',', m.Capabilities.Order(StringComparer.OrdinalIgnoreCase))).Append('\n')
            .Append(instructions.ReplaceLineEndings("\n")).Append('\n');

        foreach (ResolvedSkill skill in skills)
        {
            sb.Append(skill.Name).Append('\n').Append(skill.Body.ReplaceLineEndings("\n")).Append('\n');
        }

        if (outputType is not null)
        {
            sb.Append(AIJsonUtilities.CreateJsonSchema(outputType, serializerOptions: ContractJson.Options).GetRawText());
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
