using System.Text.Json;
using System.Text.Json.Nodes;
using CliDesignPipeline.Contracts;
using Microsoft.Extensions.AI;

namespace CliDesignPipeline.Ai.Mock;

/// <summary>
/// Canned behaviour for each agent when <c>Pipeline:Provider = Mock</c>.
/// </summary>
/// <remarks>
/// The scripts are intentionally a little bit "smart" so the offline run shows every path of the
/// workflow: the interviewer stops when asked, the developer's first attempt has a bug, the
/// tester spots it by actually reading the file through its tool, the rework loop fixes it, and
/// the release agent calls all four integration tools. Whatever product idea you type, the
/// mock builds the same small task-tracker app; switch to a real provider for real behaviour.
/// </remarks>
public static class MockScripts
{
    public static Func<MockTurn, ChatResponse> For(string agent) => agent switch
    {
        "interviewer" => Interviewer,
        "developer" => Developer,
        "tester" => Tester,
        "merge-request" => MergeRequest,
        _ => _ => Text($"(mock) No script for agent '{agent}'."),
    };

    // ---- Interviewer -----------------------------------------------------------------------

    private static readonly (string Question, string Why, string[] Options)[] Questions =
    [
        ("Who will use this day to day?",
         "Everything else (interface, permissions, wording) depends on who the user is.",
         ["Just me, as a personal tool", "My small team (2-10 people)", "Anyone in the company"]),
        ("If it could only do one thing on day one, what would that be?",
         "This becomes the core user story; everything else can wait for v2.",
         ["Add tasks and list what is open", "Assign tasks to people", "Track due dates"]),
        ("Where should the tasks be kept between runs?",
         "Storage drives both the design and what can go wrong.",
         ["A local JSON file", "SQLite", "In memory only"]),
        ("What should happen if someone adds a task with an empty title?",
         "Edge cases like this become acceptance criteria and tests.",
         ["Reject it with a clear error", "Save it as 'Untitled'", "Ignore it silently"]),
    ];

    private static ChatResponse Interviewer(MockTurn turn)
    {
        var stopRequested = turn.LastUserText.Contains("Produce the final specification now", StringComparison.Ordinal);
        var questionIndex = turn.UserMessageCount - 1;

        if (!stopRequested && questionIndex < Questions.Length)
        {
            var (q, why, options) = Questions[questionIndex];
            return Json(new InterviewTurn(false, q, why, options, null));
        }

        var feedback = turn.LastUserText.StartsWith("The stakeholder rejected", StringComparison.Ordinal)
            ? ["Revised after stakeholder feedback: " + turn.LastUserText.Split("reason:", 2).ElementAtOrDefault(1)?.Trim()]
            : Array.Empty<string>();

        return Json(new InterviewTurn(true, null, null, null, new RequirementsSpec(
            Title: "Team Task Tracker CLI",
            Summary: "A tiny command-line tool a small team uses to add tasks, list open ones and mark them done. Tasks are kept in a local JSON file.",
            PrimaryUser: "Members of a small team (2-10 people) sharing one machine or repo",
            AppKind: "console",
            UserStories:
            [
                "As a team member, I want to add a task with a title, so that it is not forgotten.",
                "As a team member, I want to list open tasks, so that I know what is left to do.",
                "As a team member, I want to mark a task as done, so that the list stays current.",
            ],
            AcceptanceCriteria:
            [
                "Given an empty store, when I run `add \"Write docs\"`, then `list` shows one open task 'Write docs' with id 1.",
                "Given a task with id 1, when I run `done 1`, then `list` no longer shows it.",
                "When I run `add \"\"` (empty or whitespace title), then I see an error and nothing is saved.",
                "Tasks survive between runs (stored in tasks.json next to the executable).",
            ],
            Assumptions: ["Single user at a time; no file locking.", "No due dates or assignees in v1.", .. feedback],
            OutOfScope: ["Assigning tasks to people", "Due dates and reminders", "A web or GUI front end"])));
    }

    // ---- Developer ---------------------------------------------------------------------------

    private static ChatResponse Developer(MockTurn turn)
    {
        var isFixRound = turn.Messages.Any(m => m.Role == ChatRole.User && m.Text.Contains("Review report", StringComparison.Ordinal));

        if (turn.HasToolResults)
        {
            return Json(isFixRound
                ? new DeveloperReport(
                    "Fixed the tester's findings: TaskStore.Add now rejects empty or whitespace titles with an ArgumentException, and Program.cs reports it as a friendly error with exit code 1.",
                    ["TaskStore.cs", "Program.cs"],
                    [])
                : new DeveloperReport(
                    "Implemented a .NET 10 console app with add/list/done commands. Tasks are records persisted to tasks.json via System.Text.Json.",
                    ["TaskTracker.csproj", "Program.cs", "TaskStore.cs", "README.md"],
                    ["Ids are sequential integers starting at 1."]));
        }

        return isFixRound
            ? Calls(
                ("read_file", new { path = "TaskStore.cs" }),
                ("write_file", new { path = "TaskStore.cs", content = GeneratedApp.TaskStoreFixed }),
                ("write_file", new { path = "Program.cs", content = GeneratedApp.ProgramFixed }))
            : Calls(
                ("list_files", new { }),
                ("write_file", new { path = "TaskTracker.csproj", content = GeneratedApp.Csproj }),
                ("write_file", new { path = "Program.cs", content = GeneratedApp.Program }),
                ("write_file", new { path = "TaskStore.cs", content = GeneratedApp.TaskStore }),
                ("write_file", new { path = "README.md", content = GeneratedApp.Readme }));
    }

    // ---- Tester -----------------------------------------------------------------------------

    private static ChatResponse Tester(MockTurn turn)
    {
        if (!turn.HasToolResults)
        {
            return Calls(
                ("list_files", new { }),
                ("read_file", new { path = "TaskStore.cs" }),
                ("read_file", new { path = "Program.cs" }),
                ("write_test_file", new { path = "tests/TaskTracker.Tests/TaskTracker.Tests.csproj", content = GeneratedApp.TestCsproj }),
                ("write_test_file", new { path = "tests/TaskTracker.Tests/TaskStoreTests.cs", content = GeneratedApp.Tests }));
        }

        // The verdict depends on what the read_file tool actually returned - i.e. on the code
        // the developer wrote, not on which round we think it is.
        var validatesTitles = turn.RecentToolResults.Any(r => r.Contains("ArgumentException", StringComparison.Ordinal));
        string[] tests = ["tests/TaskTracker.Tests/TaskTracker.Tests.csproj", "tests/TaskTracker.Tests/TaskStoreTests.cs"];

        return Json(validatesTitles
            ? new TesterReport(
                ReviewVerdict.Approved,
                "All four acceptance criteria are implemented. Empty titles are now rejected before anything is saved. Tests cover add, done, empty-title rejection and persistence.",
                [new(Severity.Low, "TaskStore.cs", "Save() writes tasks.json directly; write to a temp file and move it to avoid a corrupt file on crash (see coding standards > Storage).")],
                tests)
            : new TesterReport(
                ReviewVerdict.ChangesRequested,
                "Add/list/done work, but the empty-title acceptance criterion is not met: TaskStore.Add accepts \"\" and saves it.",
                [
                    new(Severity.High, "TaskStore.cs", "Add() does not validate the title. Reject null/empty/whitespace with ArgumentException before mutating state."),
                    new(Severity.Medium, "Program.cs", "No top-level error handling; an exception prints a stack trace. Catch ArgumentException and print a friendly message with exit code 1."),
                ],
                tests));
    }

    // ---- Merge request ------------------------------------------------------------------------

    private static ChatResponse MergeRequest(MockTurn turn)
    {
        const string Branch = "feature/DEMO-123-team-task-tracker";

        if (!turn.HasToolResults)
        {
            return Calls(
                ("create_pull_request", new
                {
                    head = Branch,
                    @base = "main",
                    title = "DEMO-123: Add Team Task Tracker CLI",
                    body = "## Summary\nAdds a small CLI to add, list and complete tasks.\n\n## Review\nApproved by Tester.",
                    draft = true,
                }),
                ("jira_add_comment", new { issueKey = "DEMO-123", comment = "Team Task Tracker CLI implemented and reviewed (Approved).\nDraft PR opened: feature/DEMO-123-team-task-tracker" }),
                ("jira_transition_issue", new { issueKey = "DEMO-123", transitionName = "In Review" }),
                ("confluence_update_page", new { heading = "DEMO-123: Team Task Tracker CLI", content = "- New console app: add, list, done\n- Tasks stored in tasks.json\n- Empty titles rejected" }));
        }

        var prUrl = turn.RecentToolResults
            .Select(r => r.StartsWith('{') ? JsonNode.Parse(r)?["html_url"]?.GetValue<string>() : null)
            .FirstOrDefault(u => u is not null);

        return Json(new MergeRequestPlan(
            Branch: Branch,
            Title: "DEMO-123: Add Team Task Tracker CLI",
            Body: """
                ## Summary
                Adds **Team Task Tracker CLI**, a .NET 10 console app to add, list and complete tasks stored in `tasks.json`.

                ## Acceptance criteria
                - [x] `add` then `list` shows the task with id 1
                - [x] `done 1` removes it from the open list
                - [x] Empty titles are rejected and nothing is saved
                - [x] Tasks persist between runs

                ## Review
                Approved by Tester after one rework round. One open `Low` finding (atomic file writes).

                ## How to test
                ```bash
                dotnet test tests/TaskTracker.Tests
                dotnet run -- add "Write docs" && dotnet run -- list
                ```
                """,
            Commands:
            [
                "cd app",
                "git init -b main   # skip if this folder is already inside your repo",
                $"git switch -c {Branch}",
                "git add TaskTracker.csproj Program.cs TaskStore.cs",
                "git commit -m \"feat: scaffold team task tracker CLI\"",
                "git add tests/",
                "git commit -m \"test: add task tracker tests\"",
                "git add README.md",
                "git commit -m \"docs: add README\"",
                "git remote add origin https://github.com/your-org/your-repo.git   # if needed",
                $"git push -u origin {Branch}",
                $"gh pr create --draft --base main --head {Branch} --title \"DEMO-123: Add Team Task Tracker CLI\" --body-file ../MERGE_REQUEST.md",
            ],
            PullRequestUrl: prUrl,
            Notes: ["create_pull_request was mocked; run the commands above to push for real."]));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse Json<T>(T value) => Text(JsonSerializer.Serialize(value, PipelineJson.Options));

    private static ChatResponse Calls(params (string Name, object Args)[] calls) =>
        new(new ChatMessage(ChatRole.Assistant,
        [
            .. calls.Select(c => new FunctionCallContent(
                callId: $"call_{Guid.NewGuid():N}"[..16],
                name: c.Name,
                arguments: JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(c.Args, PipelineJson.Options), PipelineJson.Options))),
        ]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };
}
