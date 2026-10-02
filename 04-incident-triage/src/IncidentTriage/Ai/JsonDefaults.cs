using System.Text.Encodings.Web;
using System.Text.Json;

namespace IncidentTriage.Ai;

/// <summary>
/// One <see cref="JsonSerializerOptions"/> instance shared by structured agent output, the journal and the mock.
/// </summary>
/// <remarks>
/// <c>AIAgent.RunAsync&lt;T&gt;(..., serializerOptions)</c> uses these options twice: to build the JSON
/// schema sent to the model and to deserialise the reply. Passing the same instance everywhere guarantees
/// the property names the model is told about are the ones we parse. Options instances cache reflection
/// metadata, so always reuse one rather than new-ing them per call.
/// For trimming / NativeAOT you would switch to a source-generated JsonSerializerContext.
/// </remarks>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Keep markdown and quotes readable in journal.json instead of " escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compact variant for embedding JSON inside prompts (fewer tokens than indented).</summary>
    public static JsonSerializerOptions Compact { get; } = new(Options) { WriteIndented = false };
}
