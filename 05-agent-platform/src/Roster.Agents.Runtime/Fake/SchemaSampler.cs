using System.Text.Json;
using System.Text.Json.Nodes;

namespace Roster.Agents.Runtime.Fake;

/// <summary>
/// Makes up a value that satisfies a JSON schema. The fake endpoint uses it for any typed agent it has no special
/// knowledge of, so every agent in the library can run offline and produce output of the right shape.
/// </summary>
/// <remarks>
/// It understands the subset of JSON Schema that <see cref="ContractSchemas"/> produces: <c>type</c> (including
/// <c>["string", "null"]</c> unions), <c>properties</c>, <c>items</c>, <c>enum</c>, <c>const</c>, <c>anyOf</c> /
/// <c>oneOf</c>, <c>minimum</c> / <c>maximum</c>, <c>minItems</c> and a few string formats. The values are obviously
/// samples ("sample summary"), so nobody mistakes them for a model's work.
/// </remarks>
internal static class SchemaSampler
{
    // Deep enough for any contract we write; stops a self-referencing schema from recursing forever.
    private const int MaxDepth = 8;

    public static JsonNode? Sample(JsonElement schema, string name = "value", int depth = 0)
    {
        if (depth > MaxDepth || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // A fixed set of allowed values: take the first. Covers enums such as severity.
        if (schema.TryGetProperty("const", out JsonElement constant))
        {
            return JsonNode.Parse(constant.GetRawText());
        }

        if (schema.TryGetProperty("enum", out JsonElement allowed) && allowed.GetArrayLength() > 0)
        {
            return JsonNode.Parse(allowed[0].GetRawText());
        }

        // A choice of shapes: use the first one that is not just "null".
        foreach (string keyword in (string[])["anyOf", "oneOf"])
        {
            if (schema.TryGetProperty(keyword, out JsonElement branches))
            {
                JsonElement branch = branches.EnumerateArray().FirstOrDefault(b => PrimaryType(b) != "null");
                return branch.ValueKind == JsonValueKind.Object ? Sample(branch, name, depth + 1) : null;
            }
        }

        return PrimaryType(schema) switch
        {
            "object" => SampleObject(schema, depth),
            "array" => SampleArray(schema, name, depth),
            "string" => SampleString(schema, name),
            "integer" => JsonValue.Create(SampleNumber(schema, fallback: 1, integer: true)),
            "number" => JsonValue.Create(SampleNumber(schema, fallback: 0.5, integer: false)),
            "boolean" => JsonValue.Create(true),
            _ => null,
        };
    }

    // "type" may be a single string or an array such as ["string", "null"]; the first non-null entry wins.
    private static string? PrimaryType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return schema.TryGetProperty("properties", out _) ? "object" : null;
        }

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t != "null") ?? "null"
            : type.GetString();
    }

    private static JsonObject SampleObject(JsonElement schema, int depth)
    {
        var obj = new JsonObject();
        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                obj[property.Name] = Sample(property.Value, property.Name, depth + 1);
            }
        }

        return obj;
    }

    // One item is enough to show the shape; more if the schema demands a minimum.
    private static JsonArray SampleArray(JsonElement schema, string name, int depth)
    {
        var array = new JsonArray();
        if (!schema.TryGetProperty("items", out JsonElement items))
        {
            return array;
        }

        int count = schema.TryGetProperty("minItems", out JsonElement min) && min.TryGetInt32(out int m) ? Math.Max(1, m) : 1;
        for (int i = 0; i < count; i++)
        {
            array.Add(Sample(items, name, depth + 1));
        }

        return array;
    }

    private static JsonNode SampleString(JsonElement schema, string name)
    {
        string? format = schema.TryGetProperty("format", out JsonElement f) ? f.GetString() : null;
        return format switch
        {
            "date-time" => JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")),
            "date" => JsonValue.Create(DateTime.UtcNow.ToString("yyyy-MM-dd")),
            "uri" => JsonValue.Create("https://example.com/"),
            "uuid" => JsonValue.Create(Guid.Empty.ToString()),
            _ => JsonValue.Create($"sample {name}"),
        };
    }

    // The midpoint of a declared range, else the fallback.
    private static double SampleNumber(JsonElement schema, double fallback, bool integer)
    {
        double? min = schema.TryGetProperty("minimum", out JsonElement lo) ? lo.GetDouble() : null;
        double? max = schema.TryGetProperty("maximum", out JsonElement hi) ? hi.GetDouble() : null;
        double value = (min, max) switch
        {
            ({ } a, { } b) => (a + b) / 2,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => fallback,
        };

        return integer ? Math.Round(value) : value;
    }
}
