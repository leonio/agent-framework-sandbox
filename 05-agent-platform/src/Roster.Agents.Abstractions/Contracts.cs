using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roster.Agents;

/// <summary>Marks a record as an agent input or output contract and gives it the name manifests refer to.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class AgentContractAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Marks an input field whose value came from outside (a PR description, a diff, a user message). The runtime fences it
/// and labels it untrusted when it renders the prompt, so no agent has to remember to.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field)]
public sealed class UntrustedAttribute : Attribute;

public static class ContractJson
{
    /// <summary>camelCase, enums as strings. The wire format and the format the schemas are generated for.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
