using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace MrArchitectureReview.Integrations;

/// <summary>
/// Wraps the Jira and Confluence clients as tools for the publisher agent.
/// </summary>
/// <remarks>
/// <para>
/// WHY let an agent decide which tickets/comments to create, rather than a <c>foreach</c>? Honest answer:
/// for the core rule ("one ticket per accepted Medium/High finding") a loop would be more reliable, and
/// in a production system we would probably do exactly that. The agent earns its place in the
/// <i>judgement</i> parts: writing a useful PR comment that explains rejections in plain language, and
/// spotting when a rejection reason is a reusable team rule worth recording in Confluence.
/// A good middle ground, if you want determinism: create the tickets in code, and give the agent only the
/// comment and decision-log tools.
/// </para>
/// <para>
/// Tools return short, factual strings (ids/keys). The model must not invent ticket keys, and the
/// prompt tells it to only use what tools return.
/// </para>
/// </remarks>
public sealed class PublisherTools(JiraClient jira, ConfluenceClient confluence)
{
    public IList<AITool> AsAITools() =>
    [
        AIFunctionFactory.Create(CreateJiraIssueAsync, "create_jira_issue"),
        AIFunctionFactory.Create(AppendDecisionLogAsync, "append_decision_log"),
    ];

    [Description("Create a Jira ticket for an accepted architecture finding. Returns the new ticket key.")]
    public async Task<string> CreateJiraIssueAsync(
        [Description("Ticket title, e.g. 'SEC-1: Parameterise payment lookup query'")] string summary,
        [Description("Detail, recommendation and the human reviewer's reason")] string description,
        [Description("High, Medium or Low")] string priority,
        CancellationToken cancellationToken = default)
    {
        var key = await jira.CreateIssueAsync(summary, description, priority, cancellationToken);
        return $"Created {key}";
    }

    [Description("Append a reusable team rule, learned from a rejected finding, to the architecture decision log.")]
    public async Task<string> AppendDecisionLogAsync(
        [Description("Short rule title")] string title,
        [Description("The rule and the reason, 1-3 sentences")] string text,
        CancellationToken cancellationToken = default)
    {
        var id = await confluence.AppendDecisionAsync(title, text, cancellationToken);
        return $"Decision log entry {id} added";
    }
}
