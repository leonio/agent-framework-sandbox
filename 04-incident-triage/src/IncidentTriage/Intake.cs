using IncidentTriage.Workflow;

namespace IncidentTriage;

/// <summary>Command-line switches. Anything not listed here is passed to configuration (e.g. --Ai:Provider OpenAI).</summary>
internal sealed record CliArgs(string? Repo, string? Ref, string? ReportsPath, bool AutoApprove, bool PrintMermaid, string[] ConfigArgs)
{
    public static CliArgs Parse(string[] args)
    {
        string? repo = null, gitRef = null, reports = null;
        bool yes = false, mermaid = false;
        List<string> rest = [];

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
                case "--ref" when i + 1 < args.Length: gitRef = args[++i]; break;
                case "--reports" when i + 1 < args.Length: reports = args[++i]; break;
                case "--yes" or "-y": yes = true; break;
                case "--mermaid": mermaid = true; break;
                default: rest.Add(args[i]); break;
            }
        }
        return new(repo, gitRef, reports, yes, mermaid, [.. rest]);
    }
}

/// <summary>
/// Collects the run's input: a repository, an optional commit id or tag, and one or more pasted reports.
/// </summary>
/// <remarks>
/// Kept outside the workflow on purpose: the workflow's contract is a <see cref="TriageRequest"/>, and how you
/// get one (console prompts here, a web form, a PagerDuty webhook, a Slack slash command) is the host's job.
/// That separation is what lets the same workflow run interactively today and from an alert webhook tomorrow.
/// </remarks>
internal static class Intake
{
    public static TriageRequest Collect(CliArgs cli, string sampleReportsFolder, string defaultRef)
    {
        var repo = cli.Repo ?? ConsoleUi.Ask("Repository (GitHub URL, owner/repo or local path; Enter = bundled demo repo)", "");
        var gitRef = cli.Ref ?? ConsoleUi.Ask($"Commit id or git tag (Enter = {defaultRef})", "");

        var reports = cli.ReportsPath is not null
            ? LoadReports(cli.ReportsPath)
            : PasteReports(sampleReportsFolder);

        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        return new TriageRequest(runId, new RepoSource(repo, string.IsNullOrWhiteSpace(gitRef) ? null : gitRef), reports);
    }

    private static List<IncidentReport> PasteReports(string sampleReportsFolder)
    {
        ConsoleUi.Info("""

            Paste the incident report(s): alerts, log excerpts, customer tickets, chat transcripts...
              - separate multiple reports with a line containing only ---
              - finish with a line containing only END (or Ctrl+D / Ctrl+Z)
              - or type END straight away to use the sample reports
            """);

        var lines = ConsoleUi.ReadBlock("END");
        if (lines.All(string.IsNullOrWhiteSpace))
        {
            ConsoleUi.Info($"Using the sample reports from {sampleReportsFolder}");
            return LoadReports(sampleReportsFolder);
        }
        return Split(string.Join('\n', lines));
    }

    /// <summary>A folder = one report per file; a file = reports separated by '---' lines.</summary>
    private static List<IncidentReport> LoadReports(string path) =>
        Directory.Exists(path)
            ? [.. Directory.EnumerateFiles(path).Order().Select((f, i) => new IncidentReport($"R{i + 1}", File.ReadAllText(f).Trim()))]
            : Split(File.ReadAllText(path));

    private static List<IncidentReport> Split(string text) =>
    [
        .. text.ReplaceLineEndings("\n")
            .Split("\n---\n")
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select((t, i) => new IncidentReport($"R{i + 1}", t)),
    ];
}
