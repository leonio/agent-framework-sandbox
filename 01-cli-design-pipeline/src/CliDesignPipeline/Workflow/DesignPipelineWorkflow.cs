using CliDesignPipeline.Agents;
using CliDesignPipeline.Configuration;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using CliDesignPipeline.Workflow.Executors;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;

namespace CliDesignPipeline.Workflow;

/// <summary>
/// Assembles the four agents and the human gates into one workflow graph.
/// </summary>
/// <remarks>
/// <code>
///                    ┌──────────── ask-stakeholder (human) ◀──┐
///                    ▼                                         │ InterviewQuestion
///  DesignBrief ─▶ interviewer ─────────────────────────────────┘
///                    │  ▲
///   SpecApproval-    │  │ SpecApprovalDecision
///   Request          ▼  │
///                  approve-spec (human)
///                    │
///                    │ ApprovedSpec
///                    ▼
///                developer ◀───────── rework ──────┐
///                    │ ImplementationRound         │
///                    ▼                             │
///                  tester ─────────────────────────┘
///                    │ ship
///                    ▼
///              merge-request ──▶ MergeRequestPlan (workflow output)
/// </code>
/// (Run the app and open <c>output/&lt;run&gt;/workflow.mmd</c> for the generated Mermaid version.)
/// <para>
/// <b>Why a hand-built graph instead of the prebuilt orchestrations?</b>
/// <c>AgentWorkflowBuilder</c> offers ready-made patterns:
/// </para>
/// <list type="bullet">
///   <item><c>BuildSequential(agents)</c> - A then B then C, passing the conversation along;</item>
///   <item><c>BuildConcurrent(agents)</c> - fan out the same input, aggregate the answers;</item>
///   <item><c>CreateHandoffBuilderWith(agent)</c> - agents decide among themselves who goes next;</item>
///   <item><c>CreateGroupChatBuilderWith(manager)</c> / Magentic - a manager agent orchestrates.</item>
/// </list>
/// <para>
/// None of them gives us a review loop with a code-enforced round limit <i>plus</i> typed human
/// approval gates, and in an SDLC pipeline the order of steps is policy, not something a model
/// should improvise. So: <see cref="WorkflowBuilder"/> with explicit edges. Reach for handoff or
/// Magentic when the path genuinely is not known in advance (e.g. support triage).
/// </para>
/// </remarks>
public sealed class DesignPipelineWorkflow(
    PipelineAgentFactory agents,
    WorkspaceTools workspaceTools,
    TrackerTools trackerTools,
    GitHubTools gitHubTools,
    RunWorkspace workspace,
    IOptions<PipelineOptions> options)
{
    public async Task<Microsoft.Agents.AI.Workflows.Workflow> BuildAsync(CancellationToken ct = default)
    {
        var o = options.Value;

        // --- Agents: prompt file + tools, nothing else differs ---------------------------------
        var interviewerAgent = agents.Create(AgentNames.Interviewer,
            variables: new Dictionary<string, string> { ["max_questions"] = o.MaxInterviewQuestions.ToString() });
        var developerAgent = agents.Create(AgentNames.Developer, workspaceTools.DeveloperTools());
        var testerAgent = agents.Create(AgentNames.Tester, workspaceTools.TesterTools());
        var releaseAgent = agents.Create(AgentNames.MergeRequest,
            [.. await gitHubTools.GetToolsAsync(ct), .. trackerTools.AsTools()]);

        // --- Executors: the nodes of the graph ----------------------------------------------------
        var interviewer = new RequirementsInterviewExecutor(interviewerAgent, workspace, o.MaxInterviewQuestions);
        var developer = new DeveloperExecutor(developerAgent, workspace);
        var tester = new TesterExecutor(testerAgent, workspace, o.MaxReviewRounds);
        var release = new MergeRequestExecutor(releaseAgent, workspace);

        // --- Human-in-the-loop ports -------------------------------------------------------------
        // RequestPort<TRequest, TResponse>: messages of TRequest that reach the port become
        // RequestInfoEvents for the host; the host's TResponse comes back out of the port.
        var askStakeholder = RequestPort.Create<InterviewQuestion, InterviewAnswer>("ask-stakeholder");
        var approveSpec = RequestPort.Create<SpecApprovalRequest, SpecApprovalDecision>("approve-spec");

        // --- Edges --------------------------------------------------------------------------------
        // AddEdge<T>(from, to, condition) only forwards messages of type T that satisfy the
        // condition. Typing every edge out of the interviewer documents the protocol and means a
        // new message type cannot silently leak to the wrong node.
        return new WorkflowBuilder(interviewer)
            .WithName("cli-design-pipeline")
            .WithDescription("Interview -> approve spec -> develop -> test (loop) -> merge request")
            .AddEdge<InterviewQuestion>(interviewer, askStakeholder, q => q is not null, label: "question")
            .AddEdge(askStakeholder, interviewer)
            .AddEdge<SpecApprovalRequest>(interviewer, approveSpec, r => r is not null, label: "approve?")
            .AddEdge(approveSpec, interviewer)
            .AddEdge<ApprovedSpec>(interviewer, developer, s => s is not null, label: "approved spec")
            .AddEdge(developer, tester)
            // The review loop. A cycle is fine in a workflow graph: it terminates because the
            // tester's NextStep policy is bounded by MaxReviewRounds.
            .AddEdge<ReviewOutcome>(tester, developer, r => r?.Next is NextStep.Rework, label: "rework")
            .AddEdge<ReviewOutcome>(tester, release, r => r?.Next is NextStep.Ship, label: "ship")
            .WithOutputFrom(release)
            // Observability: .WithOpenTelemetry() emits a span per super-step and executor; pair
            // it with the chat client's .UseOpenTelemetry() and you get one trace for a whole run.
            .Build();
    }
}
