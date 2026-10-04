using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Reports the git branch of a project and how much its tracked files differ from the last commit, for the
/// desktop window's branch indicator.
/// </summary>
/// <remarks>
/// The branch is read from the repository's <c>HEAD</c> file without starting a process. The counts come
/// from one <c>git diff --shortstat HEAD</c>, which covers staged and unstaged changes of tracked files in
/// the whole repository and no untracked file. When git is not installed, fails or takes longer than
/// <see cref="GitTimeout"/>, the response is still <c>ok</c> with the branch and no counts. A response is
/// kept for <see cref="CacheLifetime"/> per project folder and at most one git process runs at a time.
/// </remarks>
[NeoRpcService("projectGit", Version = 1)]
internal sealed class ProjectGitService
{
    /// <summary>Largest part of <c>HEAD</c> or of a <c>.git</c> pointer file that is read.</summary>
    internal const int MaximumHeadBytes = 4096;

    /// <summary>Largest git output accepted, in UTF-16 units; more is treated as a failed run.</summary>
    internal const int MaximumOutputUnits = 64 * 1024;

    /// <summary>Longest branch name reported, in UTF-16 units.</summary>
    internal const int MaximumBranchLength = 256;

    /// <summary>How long a response is reused for the same project folder.</summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>How long git may run before it is stopped and the counts are left out.</summary>
    internal static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    private const int MaximumCachedRoots = 64;
    private const int AbbreviatedCommitLength = 7;

    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly Func<string, CancellationToken, Task<string?>>? _run;
    private readonly Func<DateTimeOffset>? _now;
    private readonly TimeSpan _timeout;
    // Also the queue of git runs: a second request for a folder waits here and then finds the first one's response.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, ProjectGitStatusResponse Response)> _cache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ProjectGitService()
    {
    }

    /// <summary>Creates the service for an owned host; it runs the <c>git</c> found on the path.</summary>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ProjectGitService(ProjectCatalog projects, string epoch)
        : this(projects, epoch, RunGitAsync, static () => DateTimeOffset.UtcNow, GitTimeout)
    {
    }

    /// <summary>Creates the service over a literal git run and clock.</summary>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="run">
    /// Returns the output of <c>git diff --shortstat HEAD</c> for a project folder, or null when git could not
    /// answer. Its token is canceled when the request is, or after <paramref name="timeout"/>.
    /// </param>
    /// <param name="now">The clock that ages cached responses.</param>
    /// <param name="timeout">How long <paramref name="run"/> is awaited.</param>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    internal ProjectGitService(ProjectCatalog projects, string epoch, Func<string, CancellationToken, Task<string?>> run,
        Func<DateTimeOffset> now, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _projects = projects;
        _epoch = epoch;
        _run = run;
        _now = now;
        _timeout = timeout;
    }

    /// <summary>Returns the branch of a project's repository and the size of its uncommitted tracked changes.</summary>
    [NeoRpcMethod("status")]
    public async Task<ProjectGitStatusResponse> StatusAsync(ProjectGitStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_projects is null || _run is null || _now is null) return Refused("unavailable");
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Refused("stale_epoch");
        if (request.ProjectId is not { } projectId) return Refused("invalid");
        // Read-only information: an archived project is answered like any other.
        var project = await SettingsProjectScope.ResolveAsync(_projects, projectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return Refused(project.Status);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(root, out var cached) && Fresh(cached.At, _now()))
                return cached.Response.ProjectId is null ? cached.Response : cached.Response with { ProjectId = projectId };
            var response = await ReadAsync(projectId, root, _run, cancellationToken).ConfigureAwait(false);
            // Aged from the end of the run, so that a slow git is not started again at once.
            var now = _now();
            foreach (var stale in _cache.Where(entry => !Fresh(entry.Value.At, now)).Select(static entry => entry.Key).ToArray()) _cache.Remove(stale);
            if (_cache.Count >= MaximumCachedRoots) _cache.Clear();
            _cache[root] = (now, response);
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProjectGitStatusResponse> ReadAsync(string projectId, string root, Func<string, CancellationToken, Task<string?>> run,
        CancellationToken cancellationToken)
    {
        string? branch;
        bool detached;
        try
        {
            if (FindGitDirectory(root) is not { } gitDirectory || ReadBounded(Path.Combine(gitDirectory, "HEAD")) is not { } head)
                return Refused("not_repository");
            if (!TryParseHead(head, out branch, out detached)) return Refused("read_failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return Refused("read_failed"); // Never serialize exception details: they name absolute paths.
        }

        string? output = null;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_timeout);
            try
            {
                // Awaited no longer than the timeout even if the run does not observe its token.
                output = await run(root, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Git is not installed, failed or timed out: the branch is still reported.
            }
        }

        return output is not null && TryParseShortStat(output, out var changedFiles, out var insertions, out var deletions)
            ? new("ok", projectId, branch, detached, insertions, deletions, changedFiles)
            : new("ok", projectId, branch, detached, null, null, null);
    }

    /// <summary>
    /// Finds the git directory of the repository containing <paramref name="root"/>: the nearest <c>.git</c>
    /// folder at or above it, or the folder a <c>.git</c> file names (a linked worktree or a submodule).
    /// </summary>
    /// <returns>The git directory, or null when the folder is in no repository.</returns>
    internal static string? FindGitDirectory(string root)
    {
        for (var directory = Path.TrimEndingDirectorySeparator(root); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, ".git");
            if (Directory.Exists(candidate)) return candidate;
            if (ReadBounded(candidate) is not { } pointer) continue;
            var line = FirstLine(pointer);
            const string prefix = "gitdir:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var target = line[prefix.Length..].Trim();
            if (target.IsEmpty || target.ContainsAnyInRange('\0', '\u001f')) return null;
            var gitDirectory = Path.GetFullPath(target.ToString(), directory);
            return Directory.Exists(gitDirectory) ? gitDirectory : null;
        }

        return null;
    }

    /// <summary>
    /// Reads the content of a <c>HEAD</c> file: a branch (<c>ref: refs/heads/name</c>) or the commit of a
    /// detached checkout, reported by its first seven digits.
    /// </summary>
    internal static bool TryParseHead(string head, out string? branch, out bool detached)
    {
        ArgumentNullException.ThrowIfNull(head);
        branch = null;
        detached = false;
        var line = FirstLine(head);
        const string reference = "ref:";
        if (line.StartsWith(reference, StringComparison.Ordinal))
        {
            var name = line[reference.Length..].Trim();
            const string heads = "refs/heads/";
            if (name.StartsWith(heads, StringComparison.Ordinal)) name = name[heads.Length..];
            if (name.Length is 0 or > MaximumBranchLength || name.ContainsAnyInRange('\0', '\u001f') || name.Contains('\u007f')) return false;
            branch = name.ToString();
            return true;
        }

        // A SHA-1 or SHA-256 object id.
        if (line.Length is not (40 or 64) || line.ContainsAnyExcept("0123456789abcdefABCDEF")) return false;
        branch = line[..AbbreviatedCommitLength].ToString();
        detached = true;
        return true;
    }

    /// <summary>
    /// Reads the one line of <c>git diff --shortstat</c>, such as
    /// <c>3 files changed, 10 insertions(+), 2 deletions(-)</c>; git leaves out a zero part and prints
    /// nothing at all when there is no difference.
    /// </summary>
    internal static bool TryParseShortStat(string output, out int changedFiles, out int insertions, out int deletions)
    {
        ArgumentNullException.ThrowIfNull(output);
        changedFiles = insertions = deletions = 0;
        var text = output.AsSpan().Trim();
        if (text.IsEmpty) return true;
        if (text.ContainsAny('\r', '\n')) return false;
        var files = false;
        foreach (var range in text.Split(','))
        {
            var part = text[range].Trim();
            var space = part.IndexOf(' ');
            if (space <= 0 || !int.TryParse(part[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            var unit = part[(space + 1)..];
            if (unit.StartsWith("file", StringComparison.Ordinal)) (changedFiles, files) = (value, true);
            else if (unit.StartsWith("insertion", StringComparison.Ordinal)) insertions = value;
            else if (unit.StartsWith("deletion", StringComparison.Ordinal)) deletions = value;
            else return false;
        }

        return files;
    }

    /// <summary>
    /// Runs <c>git diff --shortstat HEAD</c> in a folder without a shell or any prompt and returns its output,
    /// or null when git is not installed, exits with an error (a repository without a commit, a folder git
    /// does not trust) or prints more than <see cref="MaximumOutputUnits"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">Canceled; the process and its children were stopped.</exception>
    internal static async Task<string?> RunGitAsync(string root, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in (ReadOnlySpan<string>)["-C", root, "diff", "--shortstat", "--no-ext-diff", "HEAD", "--"]) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // Never take the index lock for a status display.
        start.Environment["LC_ALL"] = "C";
        // The folder decides the repository, not variables inherited from the process that started the host.
        foreach (var inherited in (ReadOnlySpan<string>)["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"]) start.Environment.Remove(inherited);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return null; // Git is not installed.
        }

        // A pipe read cannot always be canceled: stopping the process ends it.
        using var stop = cancellationToken.Register(static state => Kill((Process)state!), process);
        try
        {
            process.StandardInput.Close();
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            var output = new StringBuilder();
            var buffer = new char[1024];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaximumOutputUnits) return null;
                output.Append(buffer, 0, read);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? output.ToString() : null;
        }
        finally
        {
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    // The first MaximumHeadBytes of a file as text, or null when there is no such file.
    private static string? ReadBounded(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[MaximumHeadBytes];
            return Encoding.UTF8.GetString(buffer[..stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)]);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<char> FirstLine(string text)
    {
        var line = text.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        return (end < 0 ? line : line[..end]).Trim();
    }

    // A clock set back makes an entry look younger than nothing: it is read again.
    private static bool Fresh(DateTimeOffset at, DateTimeOffset now) => now - at is var age && age >= TimeSpan.Zero && age < CacheLifetime;

    private static ProjectGitStatusResponse Refused(string status) => new(status, null, null, false, null, null, null);
}

/// <summary>Asks for the git branch and change counts of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required. An archived project is answered too.</param>
internal sealed record ProjectGitStatusRequest(string? ExpectedEpoch, string? ProjectId);

/// <summary>
/// <c>ok</c> with the branch, or one of <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>,
/// <c>unknown_project</c>, <c>project_unavailable</c>, <c>not_repository</c> and <c>read_failed</c> with
/// every other value unset.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project the values belong to.</param>
/// <param name="Branch">The checked-out branch, or the first seven digits of the commit when detached.</param>
/// <param name="Detached">Whether <c>HEAD</c> names a commit instead of a branch.</param>
/// <param name="Insertions">Lines added in tracked files since <c>HEAD</c>, staged or not; null when git could not answer.</param>
/// <param name="Deletions">Lines removed from tracked files since <c>HEAD</c>, staged or not; null when git could not answer.</param>
/// <param name="ChangedFiles">Tracked files that differ from <c>HEAD</c>; null when git could not answer.</param>
internal sealed record ProjectGitStatusResponse(string Status, string? ProjectId, string? Branch, bool Detached, int? Insertions, int? Deletions, int? ChangedFiles);
