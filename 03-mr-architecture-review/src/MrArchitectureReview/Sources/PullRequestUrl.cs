using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MrArchitectureReview.Sources;

/// <summary>A parsed <c>https://github.com/{owner}/{repo}/pull/{number}</c> URL.</summary>
public readonly partial record struct PullRequestUrl(string Owner, string Repository, int Number)
{
    // [GeneratedRegex] creates the matcher at compile time: no runtime regex compilation, AOT-friendly,
    // and the pattern is validated by the compiler. Prefer it over `new Regex(...)` in modern .NET.
    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)/pull/(?<n>\d+)/?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern { get; } // C# 14 / .NET 10: partial *properties* work with the generator.

    public static bool TryParse(string? value, [NotNullWhen(true)] out PullRequestUrl? url)
    {
        url = null;
        if (value is null || Pattern.Match(value.Trim()) is not { Success: true } m)
            return false;

        url = new PullRequestUrl(m.Groups["owner"].Value, m.Groups["repo"].Value, int.Parse(m.Groups["n"].ValueSpan, provider: null));
        return true;
    }

    public string CloneUrl => $"https://github.com/{Owner}/{Repository}.git";
    public override string ToString() => $"{Owner}/{Repository}#{Number}";
}
