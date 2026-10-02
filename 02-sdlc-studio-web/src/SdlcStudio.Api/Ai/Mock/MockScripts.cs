using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SdlcStudio.Api.Domain;

namespace SdlcStudio.Api.Ai.Mock;

/// <summary>
/// The scripts behind <see cref="ScriptedChatClient"/>. Each agent key maps to a function of the
/// conversation so far. Structured outputs are produced by serializing the real C# contract types,
/// which guarantees the mock always matches the JSON schema the framework expects.
/// </summary>
/// <remarks>
/// The scripts deliberately exercise the interesting paths of the workflows:
/// the agile planner's first plan has an oversized story (so the validator loops back),
/// and the code reviewer rejects the first attempt of each story's first task (so the
/// developer/reviewer loop runs twice).
/// </remarks>
internal static partial class MockScripts
{
    [GeneratedRegex(@"^Project:\s*(?<v>.+)$", RegexOptions.Multiline)] private static partial Regex ProjectLine();
    [GeneratedRegex(@"^Attempt:\s*(?<v>\d+)", RegexOptions.Multiline)] private static partial Regex AttemptLine();
    [GeneratedRegex(@"^Review round:\s*(?<v>\d+)", RegexOptions.Multiline)] private static partial Regex RoundLine();
    [GeneratedRegex(@"\b(?<k>ST-\d+)\b")] private static partial Regex StoryKey();
    [GeneratedRegex(@"Task (?<n>\d+) of (?<of>\d+): (?<title>.+)")] private static partial Regex TaskLine();

    public static ChatMessage Respond(string agentKey, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var allUserText = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        var project = Match(ProjectLine(), allUserText) ?? "Sample App";
        var toolResults = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();

        return agentKey switch
        {
            "interviewer" => Text(Interviewer(messages, project)),
            "spec-writer" => Text(FunctionalSpec(project, prompt)),
            "architect" => Text(TechnicalDesign(project, prompt.Contains("Mode: as-is"))),
            "app-analyst" => AppAnalyst(project, toolResults),
            "reviewer-product" or "reviewer-architecture" or "reviewer-qa" => Json(Review(agentKey, prompt)),
            "spec-reviser" => Text(RevisedSpec(prompt)),
            "agile-planner" => Json(AgilePlan(prompt.Contains("Validation issues"))),
            "dev-planner" => Json(DevPlan(prompt)),
            "developer" => Developer(prompt, toolResults),
            "code-reviewer" => Json(CodeReview(prompt)),
            "test-designer" => Json(TestPrompts(prompt)),
            // Agents added in the Admin area have no script; echo something sensible.
            _ => Text($"[{agentKey}] (mock) I received your request about {project} and would answer it here."),
        };
    }

    // ----- Requirements interview ("grill me") ---------------------------------------------------

    private static readonly (string Question, string Recommendation)[] InterviewScript =
    [
        ("Who are the primary users, and what is the single most important job they need the app to do?",
         "Individual users managing their own items; the core job is capturing and completing tasks quickly."),
        ("What are the must-have features for the first release? Please list no more than five.",
         "Create, edit, complete and delete items; filter by status; due dates."),
        ("What data must be stored for each item, and is any of it sensitive?",
         "Title, notes, due date, status, created/updated timestamps. Nothing sensitive."),
        ("What should the user interface be: web, mobile, or API only?",
         "A responsive web UI backed by a REST API."),
        ("Any non-functional needs: expected users, response time, accessibility, hosting?",
         "Under 1,000 users, p95 under 300 ms, WCAG 2.1 AA, single cloud region."),
        ("What is explicitly out of scope for the first release?",
         "Authentication, sharing between users, notifications and mobile apps."),
    ];

    private static string Interviewer(IReadOnlyList<ChatMessage> messages, string project)
    {
        // The first user message is the kick-off; every later user message is an answer.
        var answers = messages.Where(m => m.Role == ChatRole.User).Skip(1).Select(m => m.Text).ToList();
        var wantsToStop = answers.LastOrDefault()?.Contains("summar", StringComparison.OrdinalIgnoreCase) == true;

        if (answers.Count < InterviewScript.Length && !wantsToStop)
        {
            var (question, recommendation) = InterviewScript[answers.Count];
            var ack = answers.Count == 0 ? $"Thanks, let's pin down **{project}**." : "Noted.";
            return $"{ack}\n\n**Question {answers.Count + 1}:** {question}\n\n_Recommended answer:_ {recommendation}";
        }

        var sb = new StringBuilder("REQUIREMENTS COMPLETE\n\n");
        sb.AppendLine($"# {project}: agreed requirements\n");
        string[] sections = ["Users", "Features", "Data", "Interface", "Non-functional", "Out of scope"];
        for (var i = 0; i < sections.Length; i++)
        {
            var answer = i < answers.Count ? answers[i] : InterviewScript[i].Recommendation;
            if (answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)) answer = InterviewScript[i].Recommendation;
            sb.AppendLine($"## {sections[i]}\n{answer}\n");
        }
        return sb.ToString();
    }

    // ----- Design ---------------------------------------------------------------------------------

    private static string FunctionalSpec(string project, string prompt) => $"""
        # {project}: functional specification

        ## Overview
        {project} is a small web application that lets a user capture, organise and complete work items.

        ## Personas
        - **Individual user**: wants a fast, distraction-free list of what to do next.

        ## Functional requirements
        - **FR-1** A user can create an item with a title (required, max 200 chars), optional notes and an optional due date.
        - **FR-2** A user can edit and delete an item.
        - **FR-3** A user can mark an item complete and re-open it.
        - **FR-4** A user can filter items by status (open, completed, overdue).
        - **FR-5** The list is sorted by due date, items without a due date last.

        ## User flows
        1. Open the app, see open items, add a new item inline.
        2. Tick an item to complete it; it moves to the completed filter.

        ## Data model
        | Entity | Fields |
        | --- | --- |
        | Item | Id, Title, Notes, DueDate, Status, CreatedAt, UpdatedAt |

        ## Non-functional requirements
        - p95 API latency under 300 ms; WCAG 2.1 AA.

        ## Out of scope
        - Authentication, sharing, notifications.

        ## Open questions
        - Should completed items be archived automatically?

        <!-- generated from {prompt.Length} characters of requirements -->
        """;

    private static string TechnicalDesign(string project, bool asIs) => $"""
        # {project}: technical design{(asIs ? " (as-is)" : "")}

        ## Architecture overview
        A single ASP.NET Core minimal API serving a React SPA. One relational database.
        _Rejected alternative:_ microservices, unnecessary for one bounded context.

        ## Components
        - **Api**: minimal API endpoints `/items` (CRUD) with validation filters.
        - **Web**: React + Tailwind single page app.
        - **Data**: EF Core with a single `Items` table.

        ## API surface
        `GET /items?status=`, `POST /items`, `PUT /items/{"{id}"}`, `DELETE /items/{"{id}"}`, `POST /items/{"{id}"}/complete`

        ## Data storage
        SQL Server (LocalDB for development). _Rejected:_ document DB, the data is relational and tiny.

        ## Security
        Input validation on every endpoint; no secrets in config. Authentication is out of scope for release 1.

        ## Observability
        OpenTelemetry traces and structured logs.

        ## Risks and trade-offs
        - No auth in release 1 means the app must not be internet facing.
        """;

    private static ChatMessage AppAnalyst(string project, List<FunctionResultContent> toolResults)
    {
        // Step 1: explore. Step 2: read the most promising file. Step 3: write the spec.
        // This mirrors how a real model uses the list_files / read_file tools.
        if (toolResults.Count == 0)
            return ToolCall("list_files", new() { ["relativeDirectory"] = "." });

        if (toolResults.Count == 1)
        {
            var listing = toolResults[0].Result?.ToString() ?? "";
            var target = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ')[0])
                .FirstOrDefault(p => p.Contains("README", StringComparison.OrdinalIgnoreCase))
                ?? listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]).FirstOrDefault()
                ?? "README.md";
            return ToolCall("read_file", new() { ["relativePath"] = target });
        }

        var readSnippet = (toolResults[^1].Result?.ToString() ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
        return Text($"""
            # {project}: reverse-engineered functional specification

            ## Overview
            Generated from the existing code base. The first line of the main document reads: "{readSnippet}".

            ## Personas
            - **Existing users** of the application (inferred).

            ## Functional requirements
            - **FR-1** Users can list and view the main records the app manages (inferred from entry points).
            - **FR-2** Users can create and update records.
            - **FR-3** The app exposes an HTTP API for the features above.

            ## User flows
            1. Open the app, browse records, open one to edit it (inferred).

            ## Data model
            Derived from model/entity classes in the repository (inferred).

            ## Non-functional requirements
            - No automated performance requirements were found.

            ## Known gaps and technical debt
            - Few or no automated tests found.
            - Configuration contains environment-specific values.

            ## Open questions
            - Which features are still in use?
            """);
    }

    // ----- Review ---------------------------------------------------------------------------------

    private static ReviewFindings Review(string reviewer, string prompt)
    {
        var round = int.TryParse(Match(RoundLine(), prompt), out var r) ? r : 1;
        if (round > 1)
        {
            // Later rounds are quieter: most issues were addressed by the reviser.
            return reviewer == "reviewer-qa"
                ? new([new("Add an accessibility check to the definition of done", "Make WCAG checks part of every story's acceptance.", "Low")])
                : new([]);
        }

        return reviewer switch
        {
            "reviewer-product" => new(
            [
                new("Define what 'overdue' means", "FR-4 filters overdue items but the spec does not define time zones or end-of-day rules.", "Medium"),
                new("Clarify empty state", "No flow describes what a first-time user sees with zero items.", "Low"),
            ]),
            "reviewer-architecture" => new(
            [
                new("Add concurrency handling", "Two tabs editing the same item will silently overwrite each other. Add an ETag / rowversion.", "High"),
                new("State the backup and retention policy", "The data storage section has no backup or retention expectations.", "Medium"),
            ]),
            _ => new(
            [
                new("Add validation acceptance criteria", "FR-1 limits titles to 200 characters but no requirement says what the user sees when it is exceeded.", "High"),
                new("Specify sorting tie-breaks", "FR-5 does not define the order of items with the same due date, which makes it untestable.", "Medium"),
            ]),
        };
    }

    private static string RevisedSpec(string prompt)
    {
        var spec = Between(prompt, "## Current specification", "## Findings the human ACCEPTED") ?? "";
        var accepted = Between(prompt, "## Findings the human ACCEPTED (apply these)", "## Findings the human REJECTED") ?? "";
        var applied = accepted.Split('\n')
            .Where(l => l.TrimStart().StartsWith("- "))
            .Select(l => l.Trim()[2..].Split(" (reason:")[0])
            .ToList();

        var log = applied.Count == 0
            ? "- No accepted findings; specification unchanged."
            : string.Join("\n", applied.Select(a => $"- Applied: {a}"));

        return $"{spec.Trim()}\n\n## Change log\n{log}\n";
    }

    // ----- Planning -------------------------------------------------------------------------------

    private static AgilePlan AgilePlan(bool fixValidation) => new(
        [new(1, "Users can capture and complete items"), new(2, "Users can organise and find items")],
        [
            new("EP-1", "Manage items", "Create, edit, complete and delete items.",
            [
                new("ST-1", "Create an item", "As a user I want to add an item so that I do not forget it.",
                    ["Given a title, when I save, then the item appears in the open list.",
                     "Given a title over 200 characters, when I save, then I see a validation message."], 3, 1),
                new("ST-2", "Complete an item", "As a user I want to tick off an item so that I can see progress.",
                    ["Given an open item, when I tick it, then it moves to completed.",
                     "Given a completed item, when I re-open it, then it moves back to open."], 2, 1),
            ]),
            new("EP-2", "Find items", "Filter and sort the list.",
                fixValidation
                ? [
                    new("ST-3", "Filter by status", "As a user I want to filter by status so that I can focus.",
                        ["Given mixed items, when I pick 'open', then only open items show.",
                         "Given an overdue item, when I pick 'overdue', then it shows."], 3, 2),
                    new("ST-4", "Sort by due date", "As a user I want the list sorted by due date so that urgent items are first.",
                        ["Given items with due dates, when I open the list, then the earliest is first.",
                         "Given items without due dates, when I open the list, then they are last."], 2, 2),
                  ]
                : [
                    // Deliberately too big: the PlanValidator executor rejects stories over 8 points and
                    // loops back to the planner, which demonstrates a conditional edge cycle.
                    new("ST-3", "Filter and sort the list", "As a user I want to filter and sort so that I can focus.",
                        ["Given mixed items, when I filter, then only matching items show."], 13, 2),
                  ]),
        ]);

    private static TaskBreakdown DevPlan(string prompt)
    {
        var key = Match(StoryKey(), prompt) ?? "ST-1";
        return new(
        [
            new($"Add domain model and API endpoint for {key}", "Create the entity, validation and the minimal API endpoint. Done when the endpoint returns 2xx for valid input and 400 for invalid input.",
                [$"src/Api/Features/{Slug(key)}/Endpoint.cs"]),
            new($"Add UI for {key}", "Add the React component wired to the endpoint. Done when the acceptance criteria can be demonstrated in the browser.",
                [$"src/Web/features/{Slug(key)}.tsx"]),
        ]);
    }

    // ----- Develop --------------------------------------------------------------------------------

    private static ChatMessage Developer(string prompt, List<FunctionResultContent> toolResults)
    {
        var key = Match(StoryKey(), prompt) ?? "ST-1";
        if (toolResults.Count == 0)
            return ToolCall("update_jira_ticket", new() { ["issueKey"] = key, ["comment"] = "Agent started implementing a task for this story." });

        var attempt = int.TryParse(Match(AttemptLine(), prompt), out var a) ? a : 1;
        var task = TaskLine().Match(prompt);
        var taskNo = task.Success ? task.Groups["n"].Value : "1";
        var title = task.Success ? task.Groups["title"].Value.Trim() : "task";
        var isUi = title.StartsWith("Add UI", StringComparison.OrdinalIgnoreCase);

        var guard = attempt > 1 ? "\n        ArgumentException.ThrowIfNullOrWhiteSpace(title); // added after code review" : "";
        var file = isUi
            ? new FileContent($"src/Web/features/{Slug(key)}.tsx", $$"""
                // {{key}}: {{title}}
                export function {{Pascal(key)}}Panel() {
                  return <section aria-label="{{key}}">TODO: {{title}}</section>;
                }
                """)
            : new FileContent($"src/Api/Features/{Slug(key)}/Endpoint.cs", $$"""
                // {{key}}: {{title}}
                namespace App.Features;

                public static class {{Pascal(key)}}Endpoint
                {
                    public static IResult Handle(string title)
                    {{{guard}}
                        return title.Length > 200 ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Max 200 characters"] }) : Results.Ok();
                    }
                }
                """);

        return Json(new CodeChange([file], $"feat({key.ToLowerInvariant()}): {Lower(title)}", $"Task {taskNo}, attempt {attempt}."));
    }

    private static CodeReview CodeReview(string prompt)
    {
        var attempt = int.TryParse(Match(AttemptLine(), prompt), out var a) ? a : 1;
        var firstTask = prompt.Contains("Task 1 of");
        return attempt == 1 && firstTask
            ? new(false, ["Validate that the title is not empty (guard clause) before checking its length."])
            : new(true, []);
    }

    // ----- Testing --------------------------------------------------------------------------------

    private static TestPromptSet TestPrompts(string prompt)
    {
        var keys = StoryKey().Matches(prompt).Select(m => m.Groups["k"].Value).Distinct().ToList();
        if (keys.Count == 0) keys = ["ST-1"];
        return new([.. keys.Select(k => new TestPrompt(k, $"Tests for {k}", $"""
            You are working in the repository for this project. Write automated tests for story {k}.
            1. Read the story's acceptance criteria in plan/backlog.md.
            2. For each criterion write a happy path test, an edge case test and a negative test.
            3. Use xUnit + WebApplicationFactory for API tests and Vitest + Testing Library for UI tests.
            4. Name tests Given_When_Then and keep each test independent.
            5. Run the tests, fix any test (not product) bugs, and summarise coverage gaps you found.
            """))]);
    }

    // ----- helpers --------------------------------------------------------------------------------

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Json<T>(T value) => new(ChatRole.Assistant, JsonSerializer.Serialize(value, StudioJson.Options));

    private static ChatMessage ToolCall(string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", name, args)]);

    private static string? Match(Regex regex, string text) => regex.Match(text) is { Success: true } m
        ? (m.Groups["v"].Success ? m.Groups["v"].Value : m.Groups["k"].Value).Trim()
        : null;

    private static string? Between(string text, string start, string end)
    {
        var s = text.IndexOf(start, StringComparison.Ordinal);
        if (s < 0) return null;
        s += start.Length;
        var e = text.IndexOf(end, s, StringComparison.Ordinal);
        return e < 0 ? text[s..] : text[s..e];
    }

    private static string Slug(string s) => s.ToLowerInvariant().Replace(' ', '-');
    private static string Pascal(string s) => string.Concat(s.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
