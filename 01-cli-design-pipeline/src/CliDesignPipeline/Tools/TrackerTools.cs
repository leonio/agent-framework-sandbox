using System.ComponentModel;
using CliDesignPipeline.Configuration;
using CliDesignPipeline.Tools.Integrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CliDesignPipeline.Tools;

/// <summary>
/// The "keep the paperwork in sync" tools: Jira and Confluence, exposed to the release agent.
/// </summary>
/// <remarks>
/// <para>
/// The tool methods are thin adapters over the typed HTTP clients. Keeping that split means the
/// clients stay reusable (and unit-testable) outside of AI, while this class owns the
/// model-facing concerns: tool names, descriptions, which parameters the model may choose
/// (issue key yes; Confluence page id no - it comes from config, so the model cannot be talked
/// into editing an arbitrary page).
/// </para>
/// <para>
/// <b>Human approval for side effects.</b> These calls change shared systems. In production
/// consider wrapping them with <c>new ApprovalRequiredAIFunction(fn)</c>; the agent run then
/// pauses with a <c>FunctionApprovalRequestContent</c> that a workflow surfaces as a request,
/// exactly like the spec approval gate in this sample.
/// </para>
/// </remarks>
public sealed class TrackerTools(JiraClient jira, ConfluenceClient confluence, IOptions<IntegrationOptions> options)
{
    [Description("Add a comment to a Jira work item.")]
    public async Task<string> JiraAddComment(
        [Description("Work item key, e.g. DEMO-123.")] string issueKey,
        [Description("Comment text. Plain text, one paragraph per line.")] string comment,
        CancellationToken cancellationToken = default) =>
        await Guard(() => jira.AddCommentAsync(issueKey, comment, cancellationToken));

    [Description("Move a Jira work item to another status, by transition name (e.g. 'In Review').")]
    public async Task<string> JiraTransitionIssue(
        [Description("Work item key, e.g. DEMO-123.")] string issueKey,
        [Description("Transition name, e.g. 'In Review'.")] string transitionName,
        CancellationToken cancellationToken = default) =>
        await Guard(() => jira.TransitionAsync(issueKey, transitionName, cancellationToken));

    [Description("Append a section to the team's Confluence release-notes page.")]
    public async Task<string> ConfluenceUpdatePage(
        [Description("Section heading, e.g. 'DEMO-123: Team Task Tracker'.")] string heading,
        [Description("Section content. Plain text, one bullet per line.")] string content,
        CancellationToken cancellationToken = default) =>
        await Guard(() => confluence.AppendSectionAsync(options.Value.Confluence.PageId, heading, content, cancellationToken));

    /// <summary>Turns transport failures into a message the model can act on (and the run can survive).</summary>
    private static async Task<string> Guard(Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            return $"ERROR: {ex.StatusCode?.ToString() ?? "network"} - {ex.Message}";
        }
    }

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(JiraAddComment, "jira_add_comment"),
        AIFunctionFactory.Create(JiraTransitionIssue, "jira_transition_issue"),
        AIFunctionFactory.Create(ConfluenceUpdatePage, "confluence_update_page"),
    ];
}
