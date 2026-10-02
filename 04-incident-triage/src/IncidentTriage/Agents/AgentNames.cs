namespace IncidentTriage.Agents;

/// <summary>
/// Agent names double as prompt file names (Prompts/&lt;name&gt;.md) and as the key the offline mock uses
/// to pick a script. Kebab-case because they also show up in logs, OpenTelemetry spans and journal.json.
/// </summary>
public static class AgentNames
{
    public const string SignalExtractor = "signal-extractor";
    public const string IncidentCorrelator = "incident-correlator";
    public const string RootCauseAnalyst = "root-cause-analyst";
    public const string JiraDrafter = "jira-drafter";
    public const string PostmortemWriter = "postmortem-writer";
}
