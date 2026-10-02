using System.Text;

namespace CliDesignPipeline.Contracts;

/// <summary>
/// Markdown views of the contracts, used for the files written to the run folder and for the
/// console.
/// </summary>
/// <remarks>
/// <para>
/// This uses C# 14 <b>extension members</b> (the <c>extension(T receiver) { ... }</c> block).
/// Compared with classic <c>static string ToMarkdown(this RequirementsSpec spec)</c> methods:
/// </para>
/// <list type="bullet">
///   <item>one block per receiver type groups related members,</item>
///   <item>extension <i>properties</i> are now possible (see
///         <c>finding.IsBlocking</c> below) - previously you could only add methods,</item>
///   <item>call sites are unchanged (<c>spec.ToMarkdown()</c>), so it is a drop-in upgrade.</item>
/// </list>
/// <para>
/// Why extensions at all instead of methods on the records? The records are the agents' wire
/// contract; keeping presentation out of them means the JSON schema sent to the model stays
/// exactly the data we want, with no accidental computed properties leaking into it.
/// </para>
/// </remarks>
public static class MarkdownRendering
{
    extension(RequirementsSpec spec)
    {
        public string ToMarkdown()
        {
            var md = new StringBuilder()
                .AppendLine($"# {spec.Title}")
                .AppendLine()
                .AppendLine(spec.Summary)
                .AppendLine()
                .AppendLine($"**Primary user:** {spec.PrimaryUser}  ")
                .AppendLine($"**App kind:** {spec.AppKind}")
                .AppendLine();
            AppendList(md, "User stories", spec.UserStories);
            AppendList(md, "Acceptance criteria", spec.AcceptanceCriteria, checkbox: true);
            AppendList(md, "Assumptions", spec.Assumptions);
            AppendList(md, "Out of scope", spec.OutOfScope);
            return md.ToString();
        }
    }

    extension(ReviewFinding finding)
    {
        /// <summary>An extension <i>property</i>: new in C# 14.</summary>
        public bool IsBlocking => finding.Severity is Severity.High;
    }

    extension(ReviewOutcome outcome)
    {
        public string ToMarkdown()
        {
            var report = outcome.Report;
            var md = new StringBuilder()
                .AppendLine($"# Review - round {outcome.Round}")
                .AppendLine()
                .AppendLine($"**Verdict:** {report.Verdict}  ")
                .AppendLine($"**Next step:** {outcome.Next}")
                .AppendLine()
                .AppendLine(report.Summary)
                .AppendLine()
                .AppendLine("## Findings")
                .AppendLine();

            if (report.Findings is [])
            {
                md.AppendLine("_None._");
            }
            else
            {
                md.AppendLine("| Severity | File | Finding |").AppendLine("| --- | --- | --- |");
                foreach (var f in report.Findings)
                {
                    md.AppendLine($"| {(f.IsBlocking ? "**High**" : f.Severity)} | `{f.File}` | {f.Message} |");
                }
            }

            md.AppendLine();
            AppendList(md, "Tests written", report.TestsWritten);
            return md.ToString();
        }
    }

    extension(MergeRequestPlan plan)
    {
        public string ToMarkdown() => new StringBuilder()
            .AppendLine($"# {plan.Title}")
            .AppendLine()
            .AppendLine($"**Branch:** `{plan.Branch}`  ")
            .AppendLine($"**Pull request:** {plan.PullRequestUrl ?? "_not created_"}")
            .AppendLine()
            .AppendLine(plan.Body)
            .AppendLine()
            .AppendLine("## Push it yourself")
            .AppendLine()
            .AppendLine("```bash")
            .AppendJoin(Environment.NewLine, plan.Commands)
            .AppendLine()
            .AppendLine("```")
            .AppendLine()
            .AppendLine(plan.Notes is [] ? "" : $"## Notes{Environment.NewLine}{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", plan.Notes)}")
            .ToString();
    }

    private static void AppendList(StringBuilder md, string heading, IReadOnlyList<string> items, bool checkbox = false)
    {
        md.AppendLine($"## {heading}").AppendLine();
        if (items is [])
        {
            md.AppendLine("_None._").AppendLine();
            return;
        }

        foreach (var item in items)
        {
            md.AppendLine(checkbox ? $"- [ ] {item}" : $"- {item}");
        }

        md.AppendLine();
    }
}
