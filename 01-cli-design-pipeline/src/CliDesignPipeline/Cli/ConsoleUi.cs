namespace CliDesignPipeline.Cli;

/// <summary>
/// All console output in one place, so the rest of the code never touches <see cref="Console"/>.
/// </summary>
/// <remarks>
/// Plain <see cref="Console"/> with colours keeps the dependency list short. For a richer CLI
/// (tables, spinners, selection prompts, live layouts) Spectre.Console is the usual choice, and
/// System.CommandLine for argument parsing once you have sub-commands.
/// </remarks>
public sealed class ConsoleUi
{
    private readonly Lock _gate = new(); // C# 13 System.Threading.Lock: a dedicated, cheaper lock type.

    public void Banner(string title, string subtitle)
    {
        Write(ConsoleColor.Cyan, $"{Environment.NewLine}=== {title} ===");
        Write(ConsoleColor.DarkGray, subtitle);
    }

    public void Stage(string stage, string message) => Write(ConsoleColor.Magenta, $"[{stage}] {message}");

    public void Info(string message) => Write(ConsoleColor.Gray, message);

    public void Success(string message) => Write(ConsoleColor.Green, message);

    public void Warn(string message) => Write(ConsoleColor.Yellow, message);

    public void Error(string message) => Write(ConsoleColor.Red, message);

    /// <summary>Called by <see cref="Tools.ObservedFunction"/> for every tool invocation.</summary>
    public void ToolCall(string agent, string tool, string arguments) =>
        Write(ConsoleColor.DarkYellow, $"    {agent} -> {tool}({arguments})");

    public void Markdown(string markdown) => Write(ConsoleColor.White, markdown);

    public string Ask(string prompt)
    {
        lock (_gate)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(prompt);
            Console.ResetColor();
        }

        return Console.ReadLine()?.Trim() ?? "";
    }

    private void Write(ConsoleColor colour, string text)
    {
        // Tool calls can arrive from background threads; keep lines from interleaving colours.
        lock (_gate)
        {
            Console.ForegroundColor = colour;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
