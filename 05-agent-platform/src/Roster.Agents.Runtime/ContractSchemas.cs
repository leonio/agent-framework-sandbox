using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.AI;

namespace Roster.Agents.Runtime;

/// <summary>
/// The JSON schema of a contract type, built once and cached. The same schema is hashed into the agent's version (in
/// <see cref="AgentCatalog"/>), sent to the model (native structured output, or pasted into the prompt), and used to
/// validate what comes back. One builder for all three means the hash covers exactly what the model was asked for.
/// </summary>
public static class ContractSchemas
{
    // Shaped for OpenAI-style strict structured output, which the widest range of endpoints accepts: no extra
    // properties, every property listed as required (nullable ones stay nullable), defaults moved into descriptions.
    private static readonly AIJsonSchemaCreateOptions s_createOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            DisallowAdditionalProperties = true,
            RequireAllProperties = true,
            MoveDefaultKeywordToDescription = true,
        },
    };

    private static readonly ConcurrentDictionary<Type, ContractSchema> s_cache = new();

    public static ContractSchema For(Type contractType) => s_cache.GetOrAdd(contractType, Build);

    private static ContractSchema Build(Type type)
    {
        JsonElement json = AIJsonUtilities.CreateJsonSchema(type, serializerOptions: ContractJson.Options, inferenceOptions: s_createOptions);

        // JsonSchema.Net registers every schema it builds under a base URI. Giving each contract its own URI keeps two
        // contracts from colliding in that registry.
        JsonSchema validator = JsonSchema.FromText(json.GetRawText(), baseUri: new Uri($"urn:roster:contract:{type.FullName}"));

        string name = (type.GetCustomAttributes(typeof(AgentContractAttribute), inherit: false).FirstOrDefault() as AgentContractAttribute)?.Name ?? type.Name;
        return new ContractSchema(name, json, validator);
    }
}

/// <summary>A contract's name (as manifests refer to it), its JSON schema, and a validator built from that schema.</summary>
public sealed record ContractSchema(string Name, JsonElement Json, JsonSchema Validator)
{
    /// <summary>
    /// Checks <paramref name="instance"/> against the schema. On failure, returns a short list of what is wrong, worded
    /// so it can be shown to the model in a repair request ("/items/0/severity: value is not one of the allowed values").
    /// </summary>
    public bool TryValidate(JsonElement instance, out string errors)
    {
        EvaluationResults results = Validator.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            errors = "";
            return true;
        }

        IEnumerable<string> messages = (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Values.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {e}"))
            .Distinct()
            .Take(10);

        errors = string.Join("; ", messages);
        if (errors.Length == 0)
        {
            errors = "the value does not match the schema";
        }

        return false;
    }
}
