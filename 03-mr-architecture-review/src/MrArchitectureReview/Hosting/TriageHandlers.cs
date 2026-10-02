using MrArchitectureReview.Domain;

namespace MrArchitectureReview.Hosting;

/// <summary>
/// How the host answers the workflow's human-triage requests.
/// </summary>
/// <remarks>
/// The workflow does not know or care who answers: a console prompt, a web page, a Teams adaptive
/// card, a Slack button, or (in tests and CI) a policy. That decoupling is the point of request ports.
/// </remarks>
public interface ITriageHandler
{
    ValueTask<TriageDecision> DecideAsync(TriageRequest request, CancellationToken cancellationToken);
}

/// <summary>Interactive console triage.</summary>
public sealed class ConsoleTriageHandler(string reviewer) : ITriageHandler
{
    public ValueTask<TriageDecision> DecideAsync(TriageRequest request, CancellationToken cancellationToken)
    {
        var f = request.Finding;
        Console.WriteLine();
        Write(ConsoleColor.Cyan, $"── Finding {request.Position}/{request.Total}: {f.Id} [{f.Severity}] {f.Title}");
        Console.WriteLine($"   {f.Detail}");
        Console.WriteLine($"   File: {f.FilePath ?? "n/a"} · confidence {f.Confidence:0.00}");
        Console.WriteLine($"   Recommendation: {f.Recommendation}");

        var verdict = Ask("   [a]ccept / [r]eject / [d]efer? ", key => key switch
        {
            "a" or "accept" => Verdict.Accepted,
            "r" or "reject" => Verdict.Rejected,
            "d" or "defer" => Verdict.Deferred,
            _ => (Verdict?)null,
        });

        // A reason is required for rejections: they are the most useful signal for the feedback loop.
        string reason;
        do
        {
            Console.Write(verdict is Verdict.Rejected ? "   Why reject? (required) " : "   Reason (optional, Enter to skip): ");
            reason = (Console.ReadLine() ?? "").Trim();
        } while (verdict is Verdict.Rejected && reason.Length == 0);

        return ValueTask.FromResult(new TriageDecision(f.Id, verdict, reason, reviewer, DateTimeOffset.UtcNow));
    }

    private static T Ask<T>(string prompt, Func<string, T?> parse) where T : struct
    {
        while (true)
        {
            Console.Write(prompt);
            if (parse((Console.ReadLine() ?? "").Trim().ToLowerInvariant()) is { } value)
                return value;
        }
    }

    private static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}

/// <summary>
/// Non-interactive triage: accept Medium+ findings the model is fairly sure about, reject the rest.
/// </summary>
/// <remarks>
/// Useful for tests and CI dry runs. In real use, avoid auto-accepting: a human reason is what makes the
/// stored data valuable. An "auto" decision is labelled as such so it can be filtered out of evaluation sets.
/// </remarks>
public sealed class PolicyTriageHandler : ITriageHandler
{
    public ValueTask<TriageDecision> DecideAsync(TriageRequest request, CancellationToken cancellationToken)
    {
        var f = request.Finding;
        var accept = f.Severity >= Severity.Medium && f.Confidence >= 0.6;
        var decision = new TriageDecision(
            f.Id,
            accept ? Verdict.Accepted : Verdict.Rejected,
            accept ? "auto-triage: Medium+ severity with confidence >= 0.6" : "auto-triage: below severity/confidence threshold",
            "auto-triage",
            DateTimeOffset.UtcNow);

        Console.WriteLine($"   {f.Id} [{f.Severity}] {f.Title} => {decision.Verdict} ({decision.Reason})");
        return ValueTask.FromResult(decision);
    }
}
