using System.Collections.Frozen;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Storage;

// The read-only git commands the measure of a checkout and its state share. An answer that failed or that may have been
// cut by the runner is not known: callers never read it as "nothing there".
internal static class GitCheckoutReads
{
    private static readonly FrozenDictionary<string, string> ReadEnvironment = new Dictionary<string, string>
    {
        ["GIT_OPTIONAL_LOCKS"] = "0",
    }.ToFrozenDictionary();

    // Status may otherwise refresh the index and briefly prevent an agent or the user from writing to it.
    internal static Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => GitWorktreeService.RunGitAsync(folder, arguments, timeout, ReadEnvironment, cancellationToken);

    /// <summary>
    /// Gets how the volume of a checkout compares names, found out on the checkout itself rather than from the system (a
    /// volume of macOS can tell case apart): without case when its <c>.git</c> is also found under another case.
    /// </summary>
    /// <param name="checkoutTop">The top of the checkout, which holds <c>.git</c>.</param>
    /// <returns>Ordinal, unless the volume is known not to tell case apart.</returns>
    public static StringComparer PathComparerOf(string checkoutTop) => CheckoutPaths.ComparerOf(Path.Combine(checkoutTop, ".git"));

    /// <summary>Gets an answer of git that is one path on one line, without its line terminator: nothing else is taken off, a space at the end of a name included.</summary>
    public static string Line(string output)
        => output.EndsWith("\r\n", StringComparison.Ordinal) ? output[..^2] : output.EndsWith('\n') ? output[..^1] : output;

    /// <summary>Runs a read and gives its output, or null when git failed or the output may have been cut.</summary>
    public static async Task<string?> ReadAsync(GitRunner run, string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => await ReadOrCutAsync(run, folder, arguments, cancellationToken).ConfigureAwait(false) is { Output: { } output, Cut: false } ? output : null;

    /// <summary>Runs a read; the output is null when git failed, and <c>Cut</c> says that git answered more than the runner keeps.</summary>
    public static async Task<(string? Output, bool Cut)> ReadOrCutAsync(GitRunner run, string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var answer = await run(folder, arguments, GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!answer.Succeeded) return (null, false);
        return answer.Output.Length >= GitWorktreeService.MaximumOutputCharacters ? (null, true) : (answer.Output, false);
    }

    /// <summary>Gets whether a folder is the top of a git checkout (a project folder or a worktree), not a folder inside one.</summary>
    public static async Task<bool> IsCheckoutRootAsync(GitRunner run, string folder, CancellationToken cancellationToken)
        // Empty at the top of the working tree; fails outside one and in a bare repository.
        => await ReadAsync(run, folder, ["rev-parse", "--show-cdup"], cancellationToken).ConfigureAwait(false) is { } up
            && Line(up).Length == 0;

    /// <summary>The absolute git directory of a checkout (<c>.git</c>, or <c>.git/worktrees/&lt;name&gt;</c> for a worktree); null when it cannot be read.</summary>
    public static async Task<string?> GitDirectoryAsync(GitRunner run, string folder, CancellationToken cancellationToken)
        => await ReadAsync(run, folder, ["rev-parse", "--absolute-git-dir"], cancellationToken).ConfigureAwait(false) is { } text
            && Line(text) is { Length: > 0 } path
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : null;

    /// <summary>
    /// The paths, relative to the checkout with <c>/</c>, of the submodules <c>.gitmodules</c> names, each once, that the
    /// index holds as a gitlink, whose folder is checked out (it holds a <c>.git</c>) and is reached through no link; empty
    /// without <c>.gitmodules</c>; null when it cannot be read.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> InitializedSubmodulesAsync(GitRunner run, string folder, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(folder, ".gitmodules"))) return [];
        var answer = await run(folder, ["config", "-z", "--file", ".gitmodules", "--get-regexp", @"^submodule\..*\.path$"], GitWorktreeService.ReadTimeout,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Exit code 1: no key matches.
        if (answer.ExitCode == 1 && answer.Output.Length == 0) return [];
        if (!answer.Succeeded || answer.Output.Length >= GitWorktreeService.MaximumOutputCharacters) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var paths = new List<string>();
        var seen = new HashSet<string>(PathComparerOf(root));
        foreach (var record in answer.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // Each record is the key, a line feed, and the value.
            var newline = record.IndexOf('\n');
            if (newline < 0) continue;
            var path = record[(newline + 1)..].Trim('/');
            if (path.Length == 0) continue;
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            // A path of .gitmodules that leaves the checkout is not one of its submodules.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            path = full[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
            if (!seen.Add(path) || IsReachedThroughLink(root, path)) continue;
            if (!File.Exists(Path.Combine(full, ".git")) && !Directory.Exists(Path.Combine(full, ".git"))) continue;
            // Only a gitlink of the index is a submodule: a folder .gitmodules names is otherwise a repository of its own.
            if (await ReadAsync(run, folder, ["ls-files", "-s", "-z", "--", ":(top,literal)" + path], cancellationToken).ConfigureAwait(false) is not { } stage)
                return null;
            if (stage.Split('\0').Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal) && entry.IndexOf('\t') is var tab && tab > 0
                    && entry[(tab + 1)..] == path))
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>Gets whether a segment of a relative path below <paramref name="root"/>, the last one included, is a link.</summary>
    public static bool IsReachedThroughLink(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 || new FileInfo(current).LinkTarget is not null) return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not there, or not readable: nothing below is reached through it.
                return false;
            }
        }

        return false;
    }

    /// <summary>Joins the path of a submodule and a path inside it, both relative with <c>/</c>.</summary>
    public static string Prefixed(string prefix, string path) => prefix.Length == 0 ? path : prefix + "/" + path;

    /// <summary>Adds a measured part to a sum; false, and the sum unchanged, when it would pass <see cref="long.MaxValue"/>.</summary>
    public static bool TryAdd(ref long sum, long value)
    {
        if (value < 0 || sum > long.MaxValue - value) return false;
        sum += value;
        return true;
    }
}
