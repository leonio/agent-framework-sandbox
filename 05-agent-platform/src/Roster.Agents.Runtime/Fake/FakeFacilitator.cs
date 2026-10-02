using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Roster.Agents.Runtime.Fake;

/// <summary>
/// A scripted stand-in for a conversational agent that has tools, written for the retro facilitator. It goes through
/// the same motions a real model would, so the retro can be built and demoed offline: it calls the tools it was given
/// (through Agent Framework's function-invoking loop), asks short questions, and on "wrap up" proposes cards.
/// </summary>
/// <remarks>
/// <para>The script, one model call at a time:</para>
/// <list type="number">
/// <item>First turn of the conversation (no assistant message yet): call <c>get_timeline</c> if it is offered.</item>
/// <item>Right after the timeline comes back: ask an opening question about the first moment in it.</item>
/// <item>When the person says "wrap up" (or "that's all", "done"): call <c>propose_cards</c> with one card per thing
/// they said, then, once the tool answers, tell them the drafts are waiting for them to edit and confirm.</item>
/// <item>Otherwise: ask the next follow-up, and after three answers offer to wrap up.</item>
/// </list>
/// <para>The cards follow the shape the facilitator's instructions describe: <c>sentiment</c> (good, bad or ugly),
/// <c>text</c>, and <c>agent</c> when one agent is named. The platform's <c>propose_cards</c> tool must accept that
/// shape. Sentiment is guessed from keywords; it is a draft the person edits anyway.</para>
/// </remarks>
internal static partial class FakeFacilitator
{
    private const string TimelineTool = "get_timeline";
    private const string ProposeTool = "propose_cards";

    private static readonly string[] s_followUps =
    [
        "What made it land that way for you?",
        "Was that down to one agent in particular, or the team as a whole?",
        "Anything that went well that we should make sure keeps happening?",
    ];

    public static ChatMessage Next(IReadOnlyList<ChatMessage> messages, IList<AITool>? tools)
    {
        HashSet<string> offered = [.. (tools ?? []).OfType<AIFunctionDeclaration>().Select(t => t.Name)];
        ChatMessage? last = messages.LastOrDefault();

        // A tool just answered: react to whichever tool it was.
        if (last?.Role == ChatRole.Tool && last.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
        {
            string? toolName = FindCallName(messages, result.CallId);
            return toolName == ProposeTool
                ? Say($"I've put {CountCards(messages)} draft cards in the panel. Have a look, change anything that's not quite " +
                      "right, and confirm the ones you want to keep. Nothing is saved until you do.")
                : Say($"Thanks for doing this. Let's start with one moment from the run: {FirstMoment(result.Result)} " +
                      "How did that land for you?");
        }

        List<string> said = PersonSaid(messages);
        bool firstTurn = !messages.Any(m => m.Role == ChatRole.Assistant);

        if (firstTurn && offered.Contains(TimelineTool))
        {
            return Call(TimelineTool, new Dictionary<string, object?>());
        }

        if (said.Count > 0 && WrapUp().IsMatch(said[^1]))
        {
            List<Dictionary<string, object?>> cards = DraftCards(said[..^1]);
            return offered.Contains(ProposeTool)
                ? Call(ProposeTool, new Dictionary<string, object?> { ["cards"] = cards })
                : Say("Here's what I'd put on the cards:\n" + string.Join('\n', cards.Select(c => $"- [{c["sentiment"]}] {c["text"]}")));
        }

        // The first user message is the platform opening the retro, so answers start from the second one.
        int answers = Math.Max(0, said.Count - 1);
        if (answers == 0)
        {
            return Say("Thanks for doing this. What stood out most about how this assignment went?");
        }

        string next = s_followUps[(answers - 1) % s_followUps.Length];
        return Say(answers >= 3 ? $"{next} Or, if you've said what you wanted to, shall I wrap up and draft the cards?" : next);
    }

    // The person's messages after the platform's opener, minus the wrap-up itself, each become a card.
    private static List<Dictionary<string, object?>> DraftCards(List<string> said) =>
        [.. said.Skip(1)
            .Where(s => s.Trim().Length >= 15)
            .TakeLast(6)
            .Select(s => new Dictionary<string, object?>
            {
                ["sentiment"] = GuessSentiment(s),
                ["text"] = FirstSentences(s.Trim(), 2),
                ["agent"] = AgentName().Match(s) is { Success: true } m ? m.Value.ToLowerInvariant() : null,
            })];

    private static string GuessSentiment(string text)
    {
        string t = text.ToLowerInvariant();
        if (UglyWords().IsMatch(t))
        {
            return "ugly";
        }

        return GoodWords().IsMatch(t) && !BadWords().IsMatch(t) ? "good" : "bad";
    }

    private static List<string> PersonSaid(IReadOnlyList<ChatMessage> messages) =>
        [.. messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t))];

    private static string? FindCallName(IReadOnlyList<ChatMessage> messages, string callId) =>
        messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).LastOrDefault(c => c.CallId == callId)?.Name;

    private static int CountCards(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .LastOrDefault(c => c.Name == ProposeTool)?.Arguments is { } args
            && args.TryGetValue("cards", out object? value) && value is List<Dictionary<string, object?>> cards
            ? cards.Count
            : 0;

    // The timeline's shape belongs to the platform. Whatever it is, take its first line that reads like text.
    private static string FirstMoment(object? result)
    {
        string text = result switch
        {
            null => "",
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
            _ => JsonSerializer.Serialize(result, ContractJson.Options),
        };

        string? line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('[', ']', '{', '}', ',', '"', ' ', '-'))
            .FirstOrDefault(l => l.Count(char.IsLetter) >= 10);

        return line is null ? "the review itself." : FirstSentences(line, 1).TrimEnd('.') + ".";
    }

    private static string FirstSentences(string text, int count)
    {
        string[] parts = Sentence().Split(text);
        return string.Join(' ', parts.Take(count)).Trim() is var s && s.Length <= 280 ? s : s[..277] + "...";
    }

    private static ChatMessage Say(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Call(string name, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}"[..20], name, arguments)]);

    [GeneratedRegex(@"\b(wrap(\s|-)?up|that'?s all|i'?m done|we'?re done|done for now)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WrapUp();

    [GeneratedRegex(@"\b[a-z]+(-[a-z]+)*-(reviewer|facilitator|analyst|writer|developer|tester)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AgentName();

    [GeneratedRegex(@"\b(risky|risk|hallucinat\w*|made up|invented|insecure|unsafe|creepy|tone|slow|expensive|costly|rambl\w*)\b")]
    private static partial Regex UglyWords();

    [GeneratedRegex(@"\b(good|great|helpful|useful|right|spot on|caught|saved|nice|love)\b")]
    private static partial Regex GoodWords();

    [GeneratedRegex(@"\b(wrong|miss\w*|noise|noisy|false positive|not|didn'?t|useless|irrelevant)\b")]
    private static partial Regex BadWords();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentence();
}
