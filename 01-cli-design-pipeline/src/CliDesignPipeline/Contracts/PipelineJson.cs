using System.Text.Json;
using System.Text.Json.Serialization;

namespace CliDesignPipeline.Contracts;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> instance used for agent structured output, the
/// mock model, and the files we write.
/// </summary>
/// <remarks>
/// Using the same options everywhere matters more than which options they are: the schema the
/// model is told to follow is generated from these options, so if the deserialiser used
/// different naming or enum handling, perfectly valid model output would fail to parse.
/// Enums as strings make the schema self-describing ("Approved" rather than 0) which measurably
/// helps smaller models.
/// </remarks>
public static class PipelineJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Same settings on one line, for JSONL logs.</summary>
    public static JsonSerializerOptions Compact { get; } = new(Options) { WriteIndented = false };
}
