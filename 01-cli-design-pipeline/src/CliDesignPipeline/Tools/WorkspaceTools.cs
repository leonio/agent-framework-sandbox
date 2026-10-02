using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace CliDesignPipeline.Tools;

/// <summary>
/// File-system tools for the developer and tester agents, scoped to the run's app folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a C# method becomes a tool.</b> <see cref="AIFunctionFactory.Create(Delegate, string?, string?, System.Text.Json.JsonSerializerOptions?)"/>
/// reflects over the method, turns its parameters (and their <see cref="DescriptionAttribute"/>s)
/// into a JSON schema the model sees, and wires invocation back to the method. When the agent's
/// model returns a function call, the <c>FunctionInvokingChatClient</c> that
/// <c>ChatClientAgent</c> adds to the pipeline invokes it and sends the result back to the model -
/// we never write a tool-call loop ourselves.
/// </para>
/// <para>
/// <b>Least privilege per agent.</b> The tester gets <see cref="WriteTestFile"/>, which can
/// only write under <c>tests/</c>. It cannot "fix" production code to make its own review pass;
/// that separation of duties is enforced in code, not just asked for in the prompt. Prompts
/// are requests; tool boundaries are guarantees.
/// </para>
/// <para>
/// Alternatives: expose a real sandbox (a container or dev box) through MCP, e.g. the
/// filesystem MCP server, when agents need to run builds/tests themselves. Kept in-process
/// here so the sample has no moving parts.
/// </para>
/// </remarks>
public sealed class WorkspaceTools(RunWorkspace workspace)
{
    private const int MaxFileBytes = 64 * 1024;

    [Description("List every file in the app workspace, as relative paths.")]
    public IReadOnlyList<string> ListFiles() => workspace.ListAppFiles();

    [Description("Read a text file from the app workspace.")]
    public async Task<string> ReadFile(
        [Description("Relative path, e.g. 'src/Program.cs'.")] string path,
        CancellationToken cancellationToken = default)
    {
        var full = workspace.ResolveInApp(path);
        return File.Exists(full)
            ? await File.ReadAllTextAsync(full, cancellationToken)
            : $"ERROR: '{path}' does not exist. Call list_files to see what is there.";
    }

    [Description("Create or overwrite a text file in the app workspace.")]
    public Task<string> WriteFile(
        [Description("Relative path, e.g. 'src/Program.cs'.")] string path,
        [Description("Full file content.")] string content,
        CancellationToken cancellationToken = default) =>
        WriteAsync(path, content, requiredPrefix: null, cancellationToken);

    [Description("Create or overwrite a test file. Only paths under 'tests/' are allowed.")]
    public Task<string> WriteTestFile(
        [Description("Relative path that starts with 'tests/'.")] string path,
        [Description("Full file content.")] string content,
        CancellationToken cancellationToken = default) =>
        WriteAsync(path, content, requiredPrefix: "tests", cancellationToken);

    private async Task<string> WriteAsync(string path, string content, string? requiredPrefix, CancellationToken ct)
    {
        // Returning an error *string* (instead of throwing) lets the model read the problem and
        // correct itself on the next step. Exceptions are also surfaced to the model by
        // FunctionInvokingChatClient, but only with IncludeDetailedErrors = true, and a crafted
        // message is more useful to it than a stack trace.
        if (content.Length > MaxFileBytes)
        {
            return $"ERROR: file too large ({content.Length} chars, limit {MaxFileBytes}). Split it up.";
        }

        string full;
        try
        {
            full = workspace.ResolveInApp(path, requiredPrefix);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"ERROR: {ex.Message}";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct);
        return $"OK: wrote {content.Length} chars to {path}";
    }

    public IList<AITool> DeveloperTools() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(WriteFile, "write_file"),
    ];

    public IList<AITool> TesterTools() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(WriteTestFile, "write_test_file"),
    ];
}
