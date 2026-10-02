using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentTriage.Ai;

namespace IncidentTriage.Persistence;

[JsonConverter(typeof(JsonStringEnumConverter<JournalKind>))]
public enum JournalKind { RunStarted, Ingest, AgentOutput, ToolCall, HumanDecision, Published, RunCompleted }

/// <summary>One line in the audit trail.</summary>
/// <param name="Actor">Agent name, tool name, executor id, or the human reviewer's name.</param>
/// <param name="Subject">What it is about: a report id, a cluster id, an issue key.</param>
/// <param name="Reason">For human decisions: WHY they accepted or rejected. The most valuable field here.</param>
public sealed record JournalEntry(DateTimeOffset At, JournalKind Kind, string Actor, string? Subject, object? Payload, string? Reason = null);

/// <summary>
/// Append-only record of everything the AI produced and everything a human decided about it, saved as
/// <c>output/&lt;run-id&gt;/journal.json</c>.
/// </summary>
/// <remarks>
/// <para><b>WHY keep human reasons next to AI output?</b> "Rejected: the deploy was rolled back at 14:05, before
/// the errors started" is a labelled training/evaluation example. Collect a few hundred and you can:
/// regression-test prompt changes against them (an LLM-as-judge harness), find which runbooks mislead the
/// agent, and measure acceptance rate per agent over time. Without the reason you only know <i>that</i> it
/// was wrong, not <i>why</i>.</para>
/// <para><b>WHY not workflow state?</b> Workflow state (<c>IWorkflowContext.QueueStateUpdateAsync</c>) is for
/// data the <i>workflow</i> needs to continue, and is scoped to a run/checkpoint. The journal is for people
/// and analytics after the run, so it lives outside the graph as an injected service.</para>
/// <para><b>Production alternatives:</b> a table in SQL / Cosmos DB keyed by run id; OpenTelemetry span events
/// (the agent spans already carry prompts and tool calls if sensitive data capture is on); or Azure AI
/// Foundry evaluations / tracing, which store runs and human feedback together.</para>
/// </remarks>
public sealed class TriageJournal
{
    private readonly List<JournalEntry> _entries = [];
    private readonly Lock _gate = new(); // C# 13 System.Threading.Lock: cheaper and clearer than lock(object)

    public TriageJournal(string runId, string outputRoot)
    {
        RunId = runId;
        Folder = Path.GetFullPath(Path.Combine(outputRoot, runId));
        Directory.CreateDirectory(Folder);
    }

    public string RunId { get; }
    public string Folder { get; }
    public string FilePath => Path.Combine(Folder, "journal.json");

    public IReadOnlyList<JournalEntry> Entries { get { lock (_gate) return [.. _entries]; } }

    /// <summary>Appends and flushes. Thread-safe: fan-out branches and parallel agent calls write concurrently.</summary>
    public void Record(JournalKind kind, string actor, string? subject, object? payload, string? reason = null)
    {
        lock (_gate)
        {
            _entries.Add(new(DateTimeOffset.UtcNow, kind, actor, subject, payload, reason));
            // Rewriting the whole file each time is O(n²) but n is tiny and it means a crash mid-run still
            // leaves a readable journal. For high volume, append JSON Lines instead.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { RunId, Entries = _entries }, JsonDefaults.Options));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    /// <summary>Writes an extra artefact (ticket JSON, postmortem markdown) next to the journal.</summary>
    public string WriteArtifact(string fileName, string content)
    {
        var path = Path.Combine(Folder, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
