using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SdlcStudio.Api.Ai.Mock;

namespace SdlcStudio.Tests;

/// <summary>
/// End-to-end tests: the real API, real Agent Framework workflows and real EF Core (on SQLite), with the
/// scripted mock IChatClient standing in for the model. Because the mock sits *below* the framework, these
/// tests exercise structured output, tool calling, fan-out/fan-in, conditional loops and persistence.
/// </summary>
/// <remarks>
/// WHY end-to-end rather than per-executor unit tests? For a sample, one test that walks the whole SDLC is
/// the best proof that every edge is wired. In a real project add focused unit tests per executor too:
/// executors take an AIAgent, so you can pass a ChatClientAgent over a stub IChatClient.
/// </remarks>
public sealed class StudioApiTests(StudioApiTests.StudioFactory factory) : IClassFixture<StudioApiTests.StudioFactory>
{
    private readonly HttpClient _http = factory.CreateClient();

    public sealed class StudioFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdlc-studio-tests-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ScriptedChatClient.Latency = TimeSpan.Zero;
            Directory.CreateDirectory(_root);
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:StudioSqlite", $"Data Source={Path.Combine(_root, "studio.db")}");
            builder.UseSetting("Studio:WorkspaceRoot", Path.Combine(_root, "workspace"));
            builder.UseSetting("Llm:Provider", "Mock");
        }
    }

    [Fact]
    public async Task New_idea_runs_through_every_phase_to_done()
    {
        var id = (await PostAsync("/api/runs", new { name = "Todo Tracker", idea = "A todo app for individuals with due dates." })).GetProperty("id").GetString()!;

        // Requirements interview: answer until the interviewer summarises.
        var run = await WaitAsync(id);
        while (Status(run) == "AwaitingInput")
        {
            await PostAsync($"/api/runs/{id}/answer", new { answer = "yes" });
            run = await WaitAsync(id);
        }
        Assert.Equal(("Requirements", "AwaitingApproval"), (Phase(run), Status(run)));
        Assert.Contains(Items(run, "artifacts"), a => a.GetProperty("kind").GetString() == "requirements");

        // Design runs and flows straight into a parallel review by three reviewer agents.
        await PostAsync($"/api/runs/{id}/approve", new { reason = "complete" });
        run = await WaitAsync(id);
        Assert.Equal(("Review", "AwaitingApproval"), (Phase(run), Status(run)));
        var findings = Items(run, "findings");
        Assert.Equal(3, findings.Select(f => f.GetProperty("reviewer").GetString()).Distinct().Count());

        // Revising before deciding every finding is refused.
        var early = await _http.PostAsJsonAsync($"/api/runs/{id}/revise", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        for (var i = 0; i < findings.Count; i++)
            await PostAsync($"/api/runs/{id}/findings/{findings[i].GetProperty("id").GetInt64()}",
                new { accepted = i > 0, reason = i > 0 ? "agreed" : "out of scope" });

        await PostAsync($"/api/runs/{id}/revise", new { });
        run = await WaitAsync(id);
        Assert.Equal(2, run.GetProperty("reviewRound").GetInt32());
        var spec = Items(run, "artifacts").First(a => a.GetProperty("kind").GetString() == "functional-spec");
        Assert.Equal(2, spec.GetProperty("version").GetInt32());
        Assert.Contains("Change log", spec.GetProperty("content").GetString());

        // Agile plan: the validator loop must have split the oversized story.
        await PostAsync($"/api/runs/{id}/approve", new { reason = "specs ok" });
        run = await WaitAsync(id);
        Assert.Equal("AgilePlan", Phase(run));
        var stories = Items(run, "epics").SelectMany(e => e.GetProperty("stories").EnumerateArray()).ToList();
        Assert.All(stories, s => Assert.True(s.GetProperty("points").GetInt32() <= 8));
        Assert.Contains(Items(run, "events"), e => e.GetProperty("message").GetString()!.StartsWith("Re-planning"));

        // Reject the plan once: the reason is recorded and the phase re-runs.
        await PostAsync($"/api/runs/{id}/reject", new { reason = "Please keep sprint 1 small" });
        run = await WaitAsync(id);
        Assert.Equal(("AgilePlan", "AwaitingApproval"), (Phase(run), Status(run)));

        await PostAsync($"/api/runs/{id}/approve", new { reason = "plan ok" });
        run = await WaitAsync(id);
        Assert.Equal("DevPlan", Phase(run));

        // Develop story by story; each story ends at a merge request checkpoint.
        await PostAsync($"/api/runs/{id}/approve", new { reason = "tasks ok" });
        run = await WaitAsync(id);
        var mergeRequests = 0;
        while (Phase(run) == "Develop")
        {
            Assert.Equal("AwaitingApproval", Status(run));
            mergeRequests++;
            await PostAsync($"/api/runs/{id}/approve", new { reason = "MR looks good" });
            run = await WaitAsync(id);
        }
        Assert.Equal(stories.Count, mergeRequests);
        Assert.Contains(Items(run, "events"), e => e.GetProperty("level").GetString() == "tool");
        Assert.Contains(Items(run, "events"), e => e.GetProperty("message").GetString()!.StartsWith("Code review requested changes"));

        Assert.Equal("Testing", Phase(run));
        await PostAsync($"/api/runs/{id}/approve", new { reason = "done" });
        run = await WaitAsync(id);
        Assert.Equal(("Done", "Completed"), (Phase(run), Status(run)));
        Assert.True(run.GetProperty("decisions").GetArrayLength() >= 7);
    }

    [Fact]
    public async Task Existing_app_import_generates_specs_with_file_tools()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Directory.CreateTempSubdirectory("legacy-app").FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "# Legacy billing\nCreates invoices.", ct);
        await File.WriteAllTextAsync(Path.Combine(source, "Invoice.cs"), "public record Invoice(int Id);", ct);

        var id = (await PostAsync("/api/runs/import", new { name = "Legacy billing", path = source })).GetProperty("id").GetString()!;
        var run = await WaitAsync(id);

        Assert.Equal(("Review", "AwaitingApproval"), (Phase(run), Status(run)));
        var spec = Items(run, "artifacts").First(a => a.GetProperty("kind").GetString() == "functional-spec");
        Assert.Contains("# Legacy billing", spec.GetProperty("content").GetString());
        Assert.Contains(Items(run, "events"), e => e.GetProperty("message").GetString()!.Contains("read_file"));
    }

    [Fact]
    public async Task Invalid_requests_return_problem_details_and_admin_is_seeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _http.PostAsJsonAsync("/api/runs", new { name = "x", idea = "" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var templates = await _http.GetFromJsonAsync<JsonElement>("/api/admin/templates", ct);
        Assert.Contains(templates.EnumerateArray(), t => t.GetProperty("key").GetString() == "interviewer");
    }

    private static string Phase(JsonElement run) => run.GetProperty("run").GetProperty("phase").GetString()!;
    private static string Status(JsonElement run) => run.GetProperty("run").GetProperty("status").GetString()!;
    private static List<JsonElement> Items(JsonElement run, string name) => [.. run.GetProperty(name).EnumerateArray()];

    private async Task<JsonElement> PostAsync(string url, object body)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _http.PostAsJsonAsync(url, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>Polls until the background worker has finished the current phase.</summary>
    private async Task<JsonElement> WaitAsync(string id)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 300; i++)
        {
            var run = await _http.GetFromJsonAsync<JsonElement>($"/api/runs/{id}", ct);
            if (Status(run) == "Failed") Assert.Fail(run.GetProperty("run").GetProperty("statusMessage").GetString());
            if (Status(run) != "Running") return run;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException($"Run {id} did not leave the Running state.");
    }
}
