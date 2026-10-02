namespace IncidentTriage;

/// <summary>
/// All console I/O in one place. Executors never write to the console directly; they raise workflow
/// events and the host decides how to show them (see Program.cs). The one exception is tool-call
/// tracing from agent middleware, which has no workflow context to raise events on.
/// </summary>
/// <remarks>
/// Alternative: Spectre.Console for tables, spinners and prompts. Kept dependency-free here so the
/// sample's package list is only things that matter for agents.
/// </remarks>
internal static class ConsoleUi
{
    private static readonly Lock Gate = new(); // parallel agents write concurrently; keep lines whole

    public static void Banner(string title, string subtitle)
    {
        Write(ConsoleColor.Cyan, $"\n=== {title} ===");
        Write(ConsoleColor.DarkCyan, subtitle);
    }

    public static void Step(string step, string message) => Write(ConsoleColor.Green, $"[{step}] {message}");

    public static void Info(string message) => Write(ConsoleColor.Gray, message);

    public static void Warn(string message) => Write(ConsoleColor.Yellow, $"! {message}");

    public static void Error(string message) => Write(ConsoleColor.Red, $"x {message}");

    public static void Tool(string agent, string call) => Write(ConsoleColor.DarkYellow, $"    tool  {agent} -> {call}");

    public static void Heading(string text) => Write(ConsoleColor.White, $"\n{text}\n{new string('-', Math.Min(text.Length, 100))}");

    public static string Ask(string question, string? defaultValue = null)
    {
        lock (Gate)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(defaultValue is null ? $"{question}: " : $"{question} [{defaultValue}]: ");
            Console.ResetColor();
        }
        var answer = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(answer) ? defaultValue ?? "" : answer;
    }

    /// <summary>Reads lines until a line equal to <paramref name="terminator"/> or end of input (Ctrl+D / Ctrl+Z).</summary>
    public static List<string> ReadBlock(string terminator)
    {
        List<string> lines = [];
        while (Console.ReadLine() is { } line && line.Trim() != terminator)
            lines.Add(line);
        return lines;
    }

    private static void Write(ConsoleColor color, string text)
    {
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
