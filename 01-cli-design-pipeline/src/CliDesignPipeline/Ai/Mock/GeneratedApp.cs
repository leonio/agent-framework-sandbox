namespace CliDesignPipeline.Ai.Mock;

/// <summary>
/// The source files the mock developer and tester "write". Kept in their own file so the
/// scripts stay readable. The generated app genuinely builds and its tests pass
/// (<c>dotnet test output/&lt;run&gt;/app/tests/TaskTracker.Tests</c>) once the rework round has run.
/// </summary>
internal static class GeneratedApp
{
    public const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <Compile Remove="tests/**" />
          </ItemGroup>
        </Project>
        """;

    public const string Program = """
        using TaskTracker;

        var store = TaskStore.Load(Path.Combine(AppContext.BaseDirectory, "tasks.json"));

        switch (args)
        {
            case ["add", var title]:
                var added = store.Add(title);
                store.Save();
                Console.WriteLine($"Added #{added.Id}: {added.Title}");
                break;
            case ["list"]:
                foreach (var t in store.Open)
                    Console.WriteLine($"#{t.Id,-3} {t.Title}");
                break;
            case ["done", var id] when int.TryParse(id, out var n):
                Console.WriteLine(store.Complete(n) ? $"Completed #{n}" : $"No open task #{n}");
                store.Save();
                break;
            default:
                Console.WriteLine("Usage: tasks add \"title\" | list | done <id>");
                return 1;
        }

        return 0;
        """;

    public const string TaskStore = """
        using System.Text.Json;

        namespace TaskTracker;

        public sealed record TaskItem(int Id, string Title, bool Done = false);

        public sealed class TaskStore(string path, List<TaskItem> items)
        {
            private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

            public IEnumerable<TaskItem> Open => items.Where(t => !t.Done);

            public static TaskStore Load(string path) =>
                new(path, File.Exists(path) ? JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(path), Json) ?? [] : []);

            public TaskItem Add(string title)
            {
                var item = new TaskItem(items.Count == 0 ? 1 : items.Max(t => t.Id) + 1, title);
                items.Add(item);
                return item;
            }

            public bool Complete(int id)
            {
                var index = items.FindIndex(t => t.Id == id && !t.Done);
                if (index < 0) return false;
                items[index] = items[index] with { Done = true };
                return true;
            }

            public void Save() => File.WriteAllText(path, JsonSerializer.Serialize(items, Json));
        }
        """;

    public const string TaskStoreFixed = """
        using System.Text.Json;

        namespace TaskTracker;

        public sealed record TaskItem(int Id, string Title, bool Done = false);

        public sealed class TaskStore(string path, List<TaskItem> items)
        {
            private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

            public IEnumerable<TaskItem> Open => items.Where(t => !t.Done);

            public static TaskStore Load(string path) =>
                new(path, File.Exists(path) ? JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(path), Json) ?? [] : []);

            public TaskItem Add(string title)
            {
                if (string.IsNullOrWhiteSpace(title))
                    throw new ArgumentException("A task needs a title.", nameof(title));

                var item = new TaskItem(items.Count == 0 ? 1 : items.Max(t => t.Id) + 1, title.Trim());
                items.Add(item);
                return item;
            }

            public bool Complete(int id)
            {
                var index = items.FindIndex(t => t.Id == id && !t.Done);
                if (index < 0) return false;
                items[index] = items[index] with { Done = true };
                return true;
            }

            public void Save() => File.WriteAllText(path, JsonSerializer.Serialize(items, Json));
        }
        """;

    public const string ProgramFixed = """
        using TaskTracker;

        var store = TaskStore.Load(Path.Combine(AppContext.BaseDirectory, "tasks.json"));

        try
        {
            switch (args)
            {
                case ["add", var title]:
                    var added = store.Add(title);
                    store.Save();
                    Console.WriteLine($"Added #{added.Id}: {added.Title}");
                    break;
                case ["list"]:
                    foreach (var t in store.Open)
                        Console.WriteLine($"#{t.Id,-3} {t.Title}");
                    break;
                case ["done", var id] when int.TryParse(id, out var n):
                    Console.WriteLine(store.Complete(n) ? $"Completed #{n}" : $"No open task #{n}");
                    store.Save();
                    break;
                default:
                    Console.WriteLine("Usage: tasks add \"title\" | list | done <id>");
                    return 1;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        return 0;
        """;

    public const string Readme = """
        # Team Task Tracker CLI

        Add, list and complete tasks from the command line. Tasks are saved to `tasks.json`.

        ```bash
        dotnet run -- add "Write docs"
        dotnet run -- list
        dotnet run -- done 1
        dotnet test tests/TaskTracker.Tests
        ```
        """;

    public const string TestCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="../../TaskTracker.csproj" />
          </ItemGroup>
        </Project>
        """;

    public const string Tests = """
        using TaskTracker;
        using Xunit;

        namespace TaskTracker.Tests;

        public sealed class TaskStoreTests : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), $"tasks-{Guid.NewGuid():N}.json");

            public void Dispose() => File.Delete(_path);

            [Fact]
            public void Adding_a_task_to_an_empty_store_gives_it_id_1()
            {
                var store = TaskStore.Load(_path);

                var item = store.Add("Write docs");

                Assert.Equal(1, item.Id);
                Assert.Single(store.Open);
            }

            [Fact]
            public void Completing_a_task_removes_it_from_the_open_list()
            {
                var store = TaskStore.Load(_path);
                store.Add("Write docs");

                Assert.True(store.Complete(1));
                Assert.Empty(store.Open);
            }

            [Theory]
            [InlineData("")]
            [InlineData("   ")]
            public void Adding_a_task_with_an_empty_title_is_rejected(string title)
            {
                var store = TaskStore.Load(_path);

                Assert.Throws<ArgumentException>(() => store.Add(title));
                Assert.Empty(store.Open);
            }

            [Fact]
            public void Tasks_survive_a_reload()
            {
                var store = TaskStore.Load(_path);
                store.Add("Write docs");
                store.Save();

                var reloaded = TaskStore.Load(_path);

                Assert.Equal("Write docs", Assert.Single(reloaded.Open).Title);
            }
        }
        """;
}
