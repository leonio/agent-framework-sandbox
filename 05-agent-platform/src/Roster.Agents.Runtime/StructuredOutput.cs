using System.Text.Json;

namespace Roster.Agents.Runtime;

/// <summary>
/// The text the runner uses to ask for a typed result and to read it back. Kept in one place because the fake endpoint
/// reads the same markers (so the prompted path can be exercised offline).
/// </summary>
/// <remarks>
/// Two strategies exist for <c>chat</c> agents (design doc section 5):
/// <list type="bullet">
/// <item><b>Native</b>: the schema goes in <c>ChatOptions.ResponseFormat</c> and the endpoint enforces it.</item>
/// <item><b>Prompted</b>: for endpoints without structured output, the schema is pasted into the instructions with a
/// request for a bare JSON object. The reply may still arrive wrapped in prose or a markdown fence, so
/// <see cref="ExtractJson"/> digs the object out before it is parsed and validated.</item>
/// </list>
/// Either way the result is validated against the schema, and one repair attempt follows a bad reply.
/// </remarks>
public static class StructuredOutput
{
    /// <summary>The sentence that starts the prompted-JSON instructions. The fake endpoint looks for it.</summary>
    public const string PromptedMarker = "Respond with a single JSON object that matches this JSON schema";

    private const string SchemaFenceStart = "```json\n";
    private const string SchemaFenceEnd = "\n```";

    /// <summary>The block appended to an agent's instructions under the Prompted strategy.</summary>
    public static string PromptedInstructions(ContractSchema schema) =>
        $"""


        ## Output format

        {PromptedMarker}. Reply with the object only: no prose, no markdown fence, nothing before or after it.

        {SchemaFenceStart}{schema.Json.GetRawText()}{SchemaFenceEnd}
        """;

    /// <summary>Finds the schema inside prompted instructions. Used by the fake endpoint to answer in the right shape.</summary>
    public static bool TryReadPromptedSchema(string text, out JsonElement schema)
    {
        schema = default;
        int marker = text.IndexOf(PromptedMarker, StringComparison.Ordinal);
        int start = marker < 0 ? -1 : text.IndexOf(SchemaFenceStart, marker, StringComparison.Ordinal);
        int end = start < 0 ? -1 : text.IndexOf(SchemaFenceEnd, start + SchemaFenceStart.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        string json = text[(start + SchemaFenceStart.Length)..end];
        try
        {
            schema = JsonDocument.Parse(json).RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls the JSON object out of a model reply: drops a surrounding markdown fence and any prose before the first
    /// <c>{</c> or after the last <c>}</c>. Returns the text unchanged when there is no object, so the parse error that
    /// follows says something useful.
    /// </summary>
    public static string ExtractJson(string text)
    {
        string trimmed = text.Trim();
        int first = trimmed.IndexOf('{');
        int last = trimmed.LastIndexOf('}');
        return first >= 0 && last > first ? trimmed[first..(last + 1)] : trimmed;
    }

    /// <summary>The follow-up message for the single repair attempt, quoting what was wrong.</summary>
    public static string RepairRequest(string problem) =>
        $"""
        Your previous reply could not be used: {problem}
        Reply again with only the corrected JSON object that matches the schema. No prose, no markdown fence.
        """;
}
