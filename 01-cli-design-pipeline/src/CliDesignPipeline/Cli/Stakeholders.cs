using CliDesignPipeline.Contracts;

namespace CliDesignPipeline.Cli;

/// <summary>
/// The human in "human-in-the-loop". The workflow raises requests; something implementing this
/// answers them.
/// </summary>
/// <remarks>
/// This seam is what makes the same workflow usable from a console (here), a web UI (sample 02
/// answers the same request types from React via an API), Teams, or a test. The workflow
/// never knows who answered or how long it took; with checkpointing it can even wait days.
/// </remarks>
public interface IStakeholder
{
    ValueTask<InterviewAnswer> AnswerAsync(InterviewQuestion question, CancellationToken ct);

    ValueTask<SpecApprovalDecision> ReviewSpecAsync(SpecApprovalRequest request, CancellationToken ct);
}

/// <summary>Asks a real person at the keyboard.</summary>
public sealed class ConsoleStakeholder(ConsoleUi ui) : IStakeholder
{
    public ValueTask<InterviewAnswer> AnswerAsync(InterviewQuestion q, CancellationToken ct)
    {
        ui.Info("");
        ui.Stage("interview", $"Question {q.Number}/{q.Budget}: {q.Question}");
        ui.Info($"  why: {q.Why}");
        for (var i = 0; i < q.SuggestedAnswers.Count; i++)
        {
            ui.Info($"  {i + 1}) {q.SuggestedAnswers[i]}");
        }

        var input = ui.Ask("  your answer (number, free text, or /done to finish): ");

        // List pattern + int parsing: "2" picks suggestion 2, empty picks the first, anything else is free text.
        var text = input switch
        {
            "" when q.SuggestedAnswers is [var first, ..] => first,
            _ when int.TryParse(input, out var n) && n >= 1 && n <= q.SuggestedAnswers.Count => q.SuggestedAnswers[n - 1],
            _ => input,
        };
        return ValueTask.FromResult(new InterviewAnswer(text));
    }

    public ValueTask<SpecApprovalDecision> ReviewSpecAsync(SpecApprovalRequest request, CancellationToken ct)
    {
        ui.Banner("Proposed specification", "Nothing is built until you approve this.");
        ui.Markdown(request.Markdown);

        var approved = ui.Ask("Approve this spec? [Y/n]: ") is "" or "y" or "Y" or "yes";
        var reason = ui.Ask(approved
            ? "Optional note for the record (why it is good to go): "
            : "What is wrong or missing? (this is sent back to the interviewer): ");

        return ValueTask.FromResult(new SpecApprovalDecision(approved, reason is "" ? null : reason, Environment.UserName));
    }
}

/// <summary>Answers on its own: first suggestion, then approve. For demos and smoke tests.</summary>
public sealed class AutoStakeholder(ConsoleUi ui) : IStakeholder
{
    public ValueTask<InterviewAnswer> AnswerAsync(InterviewQuestion q, CancellationToken ct)
    {
        var answer = q.SuggestedAnswers is [var first, ..] ? first : "/done";
        ui.Stage("interview", $"Question {q.Number}/{q.Budget}: {q.Question}");
        ui.Info($"  (auto) {answer}");
        return ValueTask.FromResult(new InterviewAnswer(answer));
    }

    public ValueTask<SpecApprovalDecision> ReviewSpecAsync(SpecApprovalRequest request, CancellationToken ct)
    {
        ui.Banner("Proposed specification", "(auto-approved)");
        ui.Markdown(request.Markdown);
        return ValueTask.FromResult(new SpecApprovalDecision(true, "Auto-approved (Pipeline:AutoAnswer=true).", "auto"));
    }
}
