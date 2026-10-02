using System.Diagnostics;
using System.Text.RegularExpressions;
using IncidentTriage.Configuration;
using IncidentTriage.Workflow;

namespace IncidentTriage.Repo;

/// <summary>A checked-out copy of the code the incident is about.</summary>
/// <param name="Location">What the user typed (URL, owner/repo, or local path).</param>
/// <param name="Ref">Branch, tag or commit id that was requested (default branch when the user left it blank).</param>
/// <param name="LocalPath">Folder on disk we read from.</param>
/// <param name="HeadCommit">Commit id actually checked out, or "n/a" for non-git folders.</param>
/// <param name="IsGit">True when <see cref="LocalPath"/> is a git work tree (so history tools work).</param>
/// <param name="IsDemo">True when we fell back to the bundled demo repository.</param>
public sealed record RepoSnapshot(string Location, string Ref, string LocalPath, string HeadCommit, bool IsGit, bool IsDemo);

/// <summary>
/// Resolves the user's "repo + optional commit id / tag" into a folder on disk.
/// </summary>
/// <remarks>
/// <para><b>WHY shell out to the git CLI?</b> It is already installed on every dev box and CI agent,
/// supports every auth method the user already has configured (credential manager, SSH agent,
/// GH CLI), and handles branches, tags and raw commit ids with the same commands.</para>
/// <para><b>Alternatives considered:</b></para>
/// <list type="bullet">
///   <item>LibGit2Sharp: pure .NET API, but ships native binaries per RID and lags on auth features.</item>
///   <item>GitHub REST "download zipball" (GET /repos/{owner}/{repo}/zipball/{ref}): no git needed,
///     but you lose history, so the "what changed recently?" tool has nothing to look at.</item>
///   <item>A GitHub MCP server (github/github-mcp-server): great for an agent to browse a repo on demand,
///     but for RAG we want the whole tree locally once, indexed up front.</item>
/// </list>
/// <para><b>Security:</b> arguments go through <see cref="ProcessStartInfo.ArgumentList"/> (no shell, no
/// string concatenation), and the ref is validated, so a pasted "main; rm -rf ~" is just an invalid ref.</para>
/// </remarks>
public sealed partial class RepositoryCloner(TriageOptions options, string demoRepoPath)
{
    // Git ref names: letters, digits and . _ / - (see `git check-ref-format`). A 7-40 char hex string is a commit id.
    [GeneratedRegex(@"^[A-Za-z0-9._/\-]{1,200}$")]
    private static partial Regex SafeRef();

    // Plain "owner/repo" shorthand for GitHub.
    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9_.\-]+)/(?<repo>[A-Za-z0-9_.\-]+?)(?:\.git)?$")]
    private static partial Regex GitHubShorthand();

    public async Task<RepoSnapshot> ResolveAsync(RepoSource source, CancellationToken ct)
    {
        var requestedRef = string.IsNullOrWhiteSpace(source.Ref) ? options.DefaultRef : source.Ref.Trim();
        if (!SafeRef().IsMatch(requestedRef))
            throw new ArgumentException($"'{requestedRef}' is not a valid branch, tag or commit id.");

        // 1. Nothing given: use the bundled demo repo so the sample always has code to search.
        if (string.IsNullOrWhiteSpace(source.Location))
            return Demo("(bundled demo repo)", requestedRef);

        var location = source.Location.Trim();

        // 2. A local folder: read the working tree as-is.
        //    WHY not `git checkout <ref>` in the user's folder? Never mutate someone's checkout behind
        //    their back. If you need a ref other than what is checked out, pass the URL instead, or
        //    use `git worktree add` here to materialise the ref in a separate folder.
        if (Directory.Exists(location))
        {
            var full = Path.GetFullPath(location);
            var isGit = Directory.Exists(Path.Combine(full, ".git"));
            var head = isGit ? (await GitAsync(full, ct, "rev-parse", "HEAD")).Trim() : "n/a";
            return new RepoSnapshot(location, isGit ? "(working tree)" : "n/a", full, head, isGit, IsDemo: false);
        }

        // 3. A remote: clone just enough history at the requested ref.
        //    Full URLs (https://..., git@...) are used verbatim; "owner/repo" is expanded to GitHub.
        var url = location.Contains("://", StringComparison.Ordinal) || location.StartsWith("git@", StringComparison.Ordinal)
            ? location
            : GitHubShorthand().Match(location) is { Success: true } m
                ? $"https://github.com/{m.Groups["owner"].Value}/{m.Groups["repo"].Value}.git"
                : throw new ArgumentException($"'{location}' is not a folder, a git URL or an owner/repo name.");

        var folderName = Regex.Replace($"{url}-{requestedRef}", "[^A-Za-z0-9]+", "-").Trim('-');
        var target = Path.GetFullPath(Path.Combine(options.WorkFolder, folderName[^Math.Min(folderName.Length, 80)..].Trim('-')));

        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            // `git init` + `fetch <ref>` + `checkout FETCH_HEAD` is the one recipe that works for a
            // branch, a tag AND a bare commit id. `git clone --branch` refuses commit ids, and a full
            // clone of a big repo is wasteful when we only need the tree plus a little history.
            await GitAsync(target, ct, "init", "--quiet");
            await GitAsync(target, ct, "remote", "add", "origin", url);
            await GitAsync(target, ct, "fetch", "--quiet", "--depth", options.CloneDepth.ToString(), "origin", requestedRef);
            await GitAsync(target, ct, "checkout", "--quiet", "FETCH_HEAD");
            var head = (await GitAsync(target, ct, "rev-parse", "HEAD")).Trim();
            return new RepoSnapshot(location, requestedRef, target, head, IsGit: true, IsDemo: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Offline, private repo without credentials, typo in the ref... The workflow is still useful
            // (reports + runbooks), so degrade to the demo code rather than failing the whole run.
            // A stricter production host would surface this as a hard error instead.
            ConsoleUi.Warn($"Could not fetch {url}@{requestedRef}: {ex.Message.Split('\n')[0]}");
            ConsoleUi.Warn("Falling back to the bundled demo repository so the run can continue.");
            return Demo(location, requestedRef);
        }
    }

    private RepoSnapshot Demo(string location, string requestedRef) =>
        new(location, requestedRef, demoRepoPath, HeadCommit: "demo", IsGit: false, IsDemo: true);

    /// <summary>Runs git and returns stdout, throwing with stderr on a non-zero exit code.</summary>
    internal static async Task<string> GitAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Never let git block on an interactive credential prompt inside a console workflow.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0
            ? await stdout
            : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {(await stderr).Trim()}");
    }
}
