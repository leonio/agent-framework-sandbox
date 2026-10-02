using System.Text.Json;
using CliDesignPipeline.Contracts;
using CliDesignPipeline.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace CliDesignPipeline.Workflow.Executors;

/// <summary>
/// Step 1: the "grill-me" interview. Loops with the human through two request ports until a spec
/// is approved, then hands it to the developer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape of the loop</b>
/// <code>
///  DesignBrief ─▶ [interviewer] ─InterviewQuestion─▶ (ask-stakeholder port) ─InterviewAnswer─▶ [interviewer] ...
///                      │
///                      └─SpecApprovalRequest─▶ (approve-spec port) ─SpecApprovalDecision─▶ [interviewer]
///                                                                         approved ─▶ ApprovedSpec ─▶ [developer]
/// </code>
/// A <b>request port</b> is a node that, when it receives a message, pauses that branch and raises
/// a <c>RequestInfoEvent</c> to whoever hosts the workflow. The host's response flows back along
/// the port's out-edge as a normal message. So "ask a human" is just another edge in the graph -
/// visible in the Mermaid diagram, checkpointable, and testable by answering requests in code.
/// </para>
/// <para>
/// <b>Why a custom <see cref="Executor"/> and not the agent bound directly?</b> An
/// <see cref="AIAgent"/> can be dropped into a workflow as-is (it then speaks
/// <c>ChatMessage</c>/<c>TurnToken</c>). We wrap it because this step needs to: handle three
/// different input types, enforce a question budget in code, parse structured output, and decide
/// where to route next. Rule of thumb: bind agents directly for chat-style chains, wrap them when
/// the step has logic or typed contracts.
/// </para>
/// <para>
/// <b>Multiple input types.</b> <c>Executor&lt;TIn&gt;</c> handles one type. For several, derive
/// from <see cref="Executor"/> and register one handler per type in
/// <see cref="ConfigureProtocol"/>. (The <c>Microsoft.Agents.AI.Workflows.Generators</c> source
/// generator lets you write <c>[MessageHandler]</c> methods on a partial class instead; same result,
/// less ceremony.)
/// </para>
/// <para>
/// <b>State caveat.</b> <c>_session</c> and <c>_asked</c> are plain fields: fine for an in-process,
/// single-run CLI. If you enable checkpointing (to resume after a crash or to wait days for a
/// human) move them into workflow state (<c>QueueStateUpdateAsync</c>, serialising the session
/// with <c>agent.SerializeSessionAsync</c>) or derive from <c>StatefulExecutor&lt;TState&gt;</c>,
/// otherwise a resumed run forgets the conversation.
/// </para>
/// </remarks>
internal sealed class RequirementsInterviewExecutor(AIAgent interviewer, RunWorkspace workspace, int maxQuestions)
    : Executor("interviewer")
{
    private AgentSession? _session;
    private int _asked;
    private RequirementsSpec? _draft;

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        protocol
            .ConfigureRoutes(routes => routes
                .AddHandler<DesignBrief>(OnBriefAsync)
                .AddHandler<InterviewAnswer>(OnAnswerAsync)
                .AddHandler<SpecApprovalDecision>(OnDecisionAsync))
            // Declaring what we send lets the builder validate edges and lets visualisers label them.
            .SendsMessage<InterviewQuestion>()
            .SendsMessage<SpecApprovalRequest>()
            .SendsMessage<ApprovedSpec>();

    private async ValueTask OnBriefAsync(DesignBrief brief, IWorkflowContext context, CancellationToken ct)
    {
        await context.QueueStateUpdateAsync(PipelineState.WorkItemKey, brief.WorkItemKey, PipelineState.Scope, ct);
        await context.AddEventAsync(new StageEvent("interview", $"Starting interview for: \"{brief.Idea}\""), ct);
        await AskInterviewerAsync($"Product idea from the stakeholder: {brief.Idea}\nWork item: {brief.WorkItemKey}", context, ct);
    }

    private async ValueTask OnAnswerAsync(InterviewAnswer answer, IWorkflowContext context, CancellationToken ct)
    {
        // The budget is enforced in code, not just requested in the prompt. Models are good at
        // following instructions and bad at counting; never rely on a prompt for a hard limit.
        var mustFinish = answer.EndsInterview || _asked >= maxQuestions;
        var message = mustFinish
            ? $"{(answer.EndsInterview ? "The stakeholder asked to stop here." : $"Answer: {answer.Text}\nThe question budget is used up.")}\nProduce the final specification now (done = true), labelling anything undecided as an assumption."
            : $"Answer: {answer.Text}";

        await AskInterviewerAsync(message, context, ct);
    }

    private async ValueTask OnDecisionAsync(SpecApprovalDecision decision, IWorkflowContext context, CancellationToken ct)
    {
        // Keep the human's "why" next to the AI's output, whatever they decided.
        await workspace.RecordDecisionAsync("spec-approval", new { decision.Approved, decision.Reason, decision.DecidedBy, spec = _draft?.Title }, ct);

        if (decision.Approved && _draft is { } spec)
        {
            await workspace.WriteArtifactAsync("SPEC.md", spec.ToMarkdown(), ct);
            await context.QueueStateUpdateAsync(PipelineState.Spec, spec, PipelineState.Scope, ct);
            await context.AddEventAsync(new StageEvent("interview", "Spec approved and saved to SPEC.md"), ct);
            await context.SendMessageAsync(new ApprovedSpec(spec), ct);
            return;
        }

        await context.AddEventAsync(new StageEvent("interview", "Spec rejected; going back to the interviewer."), ct);
        await AskInterviewerAsync(
            $"The stakeholder rejected the specification. Their reason: {decision.Reason ?? "(none given)"}\n" +
            "Either ask one follow-up question or return a revised specification.",
            context, ct);
    }

    private async ValueTask AskInterviewerAsync(string message, IWorkflowContext context, CancellationToken ct)
    {
        // One session for the whole interview = the agent remembers every previous Q&A.
        _session ??= await interviewer.CreateSessionAsync(ct);

        // RunAsync<T>: Agent Framework asks the model for JSON matching InterviewTurn's schema and
        // deserialises it. If the model returns invalid JSON this throws, the executor fails, and
        // the host sees an ExecutorFailedEvent. A production system might retry once with the
        // parse error appended ("your last reply was not valid JSON because ...").
        var response = await interviewer.RunAsync<InterviewTurn>(message, _session, PipelineJson.Options, cancellationToken: ct);

        switch (response.Result)
        {
            case { Done: true, Spec: { } spec }:
                _draft = spec;
                await context.SendMessageAsync(new SpecApprovalRequest(spec, spec.ToMarkdown()), ct);
                break;

            case { Question: { Length: > 0 } question } turn:
                _asked++;
                await context.SendMessageAsync(
                    new InterviewQuestion(_asked, maxQuestions, question, turn.Why ?? "", turn.SuggestedAnswers ?? []), ct);
                break;

            default:
                throw new InvalidOperationException(
                    $"Interviewer returned neither a question nor a spec: {JsonSerializer.Serialize(response.Result, PipelineJson.Options)}");
        }
    }
}
