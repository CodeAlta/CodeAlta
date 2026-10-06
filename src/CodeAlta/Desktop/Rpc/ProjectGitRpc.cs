using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading.Channels;
using CodeAlta.Catalog;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Reports the git branch of a project and what its work tree changed since a commit: the totals for the
/// desktop window's branch indicator, and the files with their two contents for the changes view.
/// </summary>
/// <remarks>
/// <para>
/// The branch is read from the repository's <c>HEAD</c> file without starting a process. The changes come from
/// <c>git diff --raw --numstat</c> against the commit and <c>git ls-files --others</c>: staged and unstaged
/// changes of tracked files and the files git neither tracks nor ignores, in the whole repository. When git is
/// not installed, fails or takes longer than <see cref="GitTimeout"/>, the status is still <c>ok</c> with the
/// branch and no counts.
/// </para>
/// <para>
/// A list is kept per repository and comparison for four times as long as git took to produce it, between
/// <see cref="MinimumCacheLifetime"/> and <see cref="MaximumCacheLifetime"/>, so a page may ask every second
/// or two without a slow repository being read all the time. One list is read at a time.
/// </para>
/// </remarks>
[NeoRpcService("projectGit", Version = 1)]
internal sealed class ProjectGitService
{
    /// <summary>Largest part of <c>HEAD</c> or of a <c>.git</c> pointer file that is read.</summary>
    internal const int MaximumHeadBytes = 4096;

    /// <summary>Largest list git may print, in bytes; what follows is left out and the list says so.</summary>
    internal const int MaximumListBytes = 4 * 1024 * 1024;

    /// <summary>Largest content shown for one side of a file, in bytes.</summary>
    internal const int MaximumFileBytes = 1024 * 1024;

    /// <summary>Longest branch name reported, in UTF-16 units.</summary>
    internal const int MaximumBranchLength = 256;

    /// <summary>The comparison with the last commit: what is not committed yet.</summary>
    internal const string HeadComparison = "head";

    /// <summary>The comparison with the commit the branch left its base branch at: what the branch changed.</summary>
    internal const string BranchComparison = "branch";

    /// <summary>The shortest time a list is reused for the same repository.</summary>
    internal static readonly TimeSpan MinimumCacheLifetime = TimeSpan.FromSeconds(1);

    /// <summary>The longest time a list is reused, however slow git was.</summary>
    internal static readonly TimeSpan MaximumCacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>How long a list that git could not produce is reused before git is tried again.</summary>
    internal static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>How long the base of a branch (its upstream and where it left it) is reused.</summary>
    internal static readonly TimeSpan BaseCacheLifetime = TimeSpan.FromSeconds(10);

    /// <summary>How long the files of a commit, which never change, are reused.</summary>
    internal static readonly TimeSpan CommitCacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The comparison of a commit with its parent: what that commit changed.</summary>
    internal const string CommitComparison = "commit";

    /// <summary>Most commits of the history one answer holds.</summary>
    internal const int MaximumCommits = 200;

    /// <summary>The commits answered when a page asks for no number.</summary>
    internal const int DefaultCommits = 20;

    /// <summary>How long git may run for one list or one file before it is stopped.</summary>
    internal static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    private const int MaximumCachedRoots = 64;
    private const int MaximumCountedCache = 4096;
    private const int AbbreviatedCommitLength = 7;

    private readonly ProjectCatalog? _projects;
    private readonly string? _epoch;
    private readonly Func<string, IReadOnlyList<string>, int, CancellationToken, Task<GitOutput?>>? _run;
    private readonly Func<DateTimeOffset>? _now;
    private readonly TimeSpan _timeout;
    private readonly DesktopChangesView? _view;
    // Also the queue of git runs: a second request for a repository waits here and then finds the first one's list.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, TimeSpan Lifetime, GitChangeSet? Changes)> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, BranchBase? Base)> _bases = new(StringComparer.Ordinal);
    // The line count of an untracked file, for as long as its length and last write time stay the same.
    private readonly Dictionary<string, (long Length, long WriteTicks, int? Lines)> _counted = new(StringComparer.Ordinal);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ProjectGitService()
    {
    }

    /// <summary>Creates the service for an owned host; it runs the <c>git</c> found on the path.</summary>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="view">Where an <c>alta</c> command asks for the changes of a project to be shown, if it can.</param>
    /// <exception cref="ArgumentNullException"><paramref name="projects"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ProjectGitService(ProjectCatalog projects, string epoch, DesktopChangesView? view = null)
        : this(projects, epoch, RunGitAsync, static () => DateTimeOffset.UtcNow, GitTimeout, view)
    {
    }

    /// <summary>Creates the service over a literal git run and clock.</summary>
    /// <param name="projects">The host's project catalog; it maps a project id to its folder.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="run">
    /// Runs git in a work tree with arguments and returns at most the given number of bytes of its output, or
    /// null when git could not answer. Its token is canceled when the request is, or after <paramref name="timeout"/>.
    /// </param>
    /// <param name="now">The clock that ages the lists and measures how long git took.</param>
    /// <param name="timeout">How long <paramref name="run"/> is awaited for one list or one file.</param>
    /// <param name="view">Where an <c>alta</c> command asks for the changes of a project to be shown, if it can.</param>
    /// <exception cref="ArgumentNullException">A required value is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    internal ProjectGitService(ProjectCatalog projects, string epoch, Func<string, IReadOnlyList<string>, int, CancellationToken, Task<GitOutput?>> run,
        Func<DateTimeOffset> now, TimeSpan timeout, DesktopChangesView? view = null)
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
        _view = view;
    }

    /// <summary>Returns the branch of a project's repository and the size of its uncommitted changes.</summary>
    [NeoRpcMethod("status")]
    public async Task<ProjectGitStatusResponse> StatusAsync(ProjectGitStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (repository, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (repository is null) return new(refusal, null, null, false, null, null, null);
        var changes = await ChangesAsync(repository, null, cancellationToken).ConfigureAwait(false);
        return new("ok", repository.ProjectId, repository.Branch, repository.Detached, changes?.Insertions, changes?.Deletions, changes?.Files.Length);
    }

    /// <summary>
    /// Lists the files a project's work tree changed, against the last commit or against the commit its branch
    /// left its base branch at. A page that names the list it already has gets <c>unchanged</c> and nothing else.
    /// </summary>
    [NeoRpcMethod("changes")]
    public async Task<ProjectGitChangesResponse> ChangesAsync(ProjectGitChangesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (repository, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (repository is null) return ProjectGitChangesResponse.Refused(refusal);
        if (!ValidComparison(request.Comparison, request.Commit)) return ProjectGitChangesResponse.Refused("invalid");
        var branchBase = await BranchBaseAsync(repository, cancellationToken).ConfigureAwait(false);
        // Without a base branch, or on the base itself, the branch changed nothing but what is not committed.
        var comparison = request.Comparison == CommitComparison ? CommitComparison
            : request.Comparison == BranchComparison && branchBase is { Ahead: > 0 } ? BranchComparison : HeadComparison;
        var changes = comparison == CommitComparison
            ? await CommitChangesAsync(repository, request.Commit!, cancellationToken).ConfigureAwait(false)
            : await ChangesAsync(repository, comparison == BranchComparison ? branchBase : null, cancellationToken).ConfigureAwait(false);
        if (changes is null) return ProjectGitChangesResponse.Refused("git_failed") with { ProjectId = repository.ProjectId, Branch = repository.Branch, Detached = repository.Detached };
        var revision = ProjectGitChanges.Hash(string.Join('\n', changes.Revision, repository.WorkTree, repository.Prefix, repository.Branch, repository.Detached,
            comparison, request.Comparison == CommitComparison ? request.Commit : null, branchBase?.Reference, branchBase?.Ahead));
        if (string.Equals(request.KnownRevision, revision, StringComparison.Ordinal))
            return ProjectGitChangesResponse.Refused("unchanged") with { ProjectId = repository.ProjectId, Revision = revision };
        return new("ok", repository.ProjectId, revision, repository.WorkTree, repository.Prefix, repository.Branch, repository.Detached, comparison,
            branchBase?.Reference, branchBase?.Ahead,
            [.. changes.Files.Select(static file => new ProjectGitChange(file.Path, file.OriginalPath, file.Status, file.Insertions, file.Deletions, file.Binary, file.Revision))],
            changes.Insertions, changes.Deletions, changes.Truncated);
    }

    /// <summary>
    /// Lists the newest commits of the current branch, newest first. A page that names the list it already has
    /// gets <c>unchanged</c> and nothing else.
    /// </summary>
    [NeoRpcMethod("commits")]
    public async Task<ProjectGitCommitsResponse> CommitsAsync(ProjectGitCommitsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (repository, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (repository is null) return new(refusal, null, null, null, false);
        var limit = Math.Clamp(request.Limit ?? DefaultCommits, 1, MaximumCommits);
        // One more than asked tells whether there are older ones. A repository without a commit has no history.
        var output = await RunAsync(repository.WorkTree, ProjectGitChanges.LogArguments(limit + 1), MaximumListBytes, cancellationToken).ConfigureAwait(false);
        var commits = output is { } log ? ProjectGitChanges.ParseLog(log.Bytes) : [];
        var more = commits.Count > limit;
        if (more) commits.RemoveRange(limit, commits.Count - limit);
        var revision = ProjectGitChanges.Hash(string.Join('\n', commits.Select(static commit => commit.Id)) + (more ? "\n+" : ""));
        return string.Equals(request.KnownRevision, revision, StringComparison.Ordinal)
            ? new("unchanged", repository.ProjectId, revision, null, false)
            : new("ok", repository.ProjectId, revision, [.. commits], more);
    }

    /// <summary>
    /// Returns both contents of one changed file: the one of the commit and the one of the work tree. Only a
    /// file of the current list is read.
    /// </summary>
    [NeoRpcMethod("file", TimeoutMilliseconds = 30_000)]
    public async Task<ProjectGitFileResponse> FileAsync(ProjectGitFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (repository, refusal) = await OpenAsync(request.ExpectedEpoch, request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (repository is null) return ProjectGitFileResponse.Refused(refusal);
        if (!ValidComparison(request.Comparison, request.Commit) || request.Path is not { Length: > 0 and <= 1024 } path)
            return ProjectGitFileResponse.Refused("invalid");
        var committed = request.Comparison == CommitComparison;
        var branchBase = request.Comparison == BranchComparison ? await BranchBaseAsync(repository, cancellationToken).ConfigureAwait(false) : null;
        var changes = committed
            ? await CommitChangesAsync(repository, request.Commit!, cancellationToken).ConfigureAwait(false)
            : await ChangesAsync(repository, branchBase is { Ahead: > 0 } ? branchBase : null, cancellationToken).ConfigureAwait(false);
        if (changes is null) return ProjectGitFileResponse.Refused("git_failed");
        if (Array.Find(changes.Files, file => string.Equals(file.Path, path, StringComparison.Ordinal)) is not { } change) return ProjectGitFileResponse.Refused("not_changed");

        string? original = null, modified = null;
        var originalState = "absent";
        if (change.OriginalBlob is { } blob) (original, originalState) = await BlobAsync(repository, blob, cancellationToken).ConfigureAwait(false);
        // A submodule is a commit on both sides, not a text.
        else if (change.Binary && change.Status is not ("added" or "untracked")) originalState = "binary";

        var modifiedState = "absent";
        if (committed)
        {
            // Both sides of a commit are objects of the repository: the work tree is not read.
            if (change.Blob is { } next) (modified, modifiedState) = await BlobAsync(repository, next, cancellationToken).ConfigureAwait(false);
            else if (change.Binary && change.Status != "deleted") modifiedState = "binary";
            return new("ok", repository.ProjectId, change.Path, change.Revision, original, originalState, modified, modifiedState);
        }

        var stamp = ProjectGitChanges.Stamp(repository.WorkTree, change.Path);
        if (change.Status != "deleted")
        {
            var (bytes, state) = ProjectGitChanges.ReadWorkTreeFile(repository.WorkTree, change.Path, MaximumFileBytes);
            (modified, modifiedState) = bytes is null ? (null, state) : ProjectGitChanges.DecodeText(bytes) is { } text ? (text, "text") : (null, "binary");
        }

        // The stamp is the one from before the read: a file written meanwhile is read again by the next list.
        return new("ok", repository.ProjectId, change.Path, (change with { Stamp = stamp }).Revision, original, originalState, modified, modifiedState);
    }

    /// <summary>The requests of <c>alta diff show</c> for this page, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<ProjectGitShowEvent> Watch(ProjectGitWatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.ProjectGitShowEvent);
    }

    internal async IAsyncEnumerable<ProjectGitShowEvent> WatchAsync(ProjectGitWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_view is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        // A page that does not read keeps the newest requests.
        var requests = Channel.CreateBounded<ProjectGitShowEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _view.Watch(value => requests.Writer.TryWrite(value));
        await foreach (var value in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    // The repository of a project a request names, or why there is none to answer for.
    private async Task<(Repository? Repository, string Refusal)> OpenAsync(string? expectedEpoch, string? requestedProject, CancellationToken cancellationToken)
    {
        if (_projects is null || _run is null || _now is null) return (null, "unavailable");
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return (null, "stale_epoch");
        if (requestedProject is not { } projectId) return (null, "invalid");
        // Read-only information: an archived project is answered like any other.
        var project = await SettingsProjectScope.ResolveAsync(_projects, projectId, allowArchived: true, cancellationToken).ConfigureAwait(false);
        if (project.Root is not { } root) return (null, project.Status);
        try
        {
            if (FindRepository(root) is not var (workTree, gitDirectory) || ReadBounded(Path.Combine(gitDirectory, "HEAD")) is not { } head)
                return (null, "not_repository");
            if (!TryParseHead(head, out var branch, out var detached)) return (null, "read_failed");
            // The project folder inside the work tree: empty for the work tree itself, otherwise ending with a slash.
            var relative = Path.GetRelativePath(workTree, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).Replace('\\', '/');
            var prefix = relative is "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? "" : relative + "/";
            return (new(projectId, workTree, prefix, branch!, detached), "ok");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return (null, "read_failed"); // Never serialize exception details: they name absolute paths.
        }
    }

    // The changes of a work tree against the last commit or the base of its branch: the kept list while it is
    // young enough, otherwise a new one. Null when git could not answer.
    private Task<GitChangeSet?> ChangesAsync(Repository repository, BranchBase? branchBase, CancellationToken cancellationToken)
        => CachedAsync(repository.WorkTree + "\n" + branchBase?.Commit, null, token => ReadChangesAsync(repository.WorkTree, branchBase?.Commit, token), cancellationToken);

    // One list at a time; a list is kept for `lifetime`, or by default for longer the slower git was.
    private async Task<GitChangeSet?> CachedAsync(string key, TimeSpan? lifetime, Func<CancellationToken, Task<GitChangeSet?>> read, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = _now!();
            if (_cache.TryGetValue(key, out var cached) && Fresh(cached.At, started, cached.Lifetime)) return cached.Changes;
            GitChangeSet? changes = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_timeout);
                try
                {
                    // Awaited no longer than the timeout even if a run does not observe its token.
                    changes = await read(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
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

            // Aged from the end of the run and for longer after a slow one, so that git is not started again at once.
            var now = _now();
            var took = now - started;
            var kept = changes is null ? FailureCacheLifetime
                : lifetime ?? TimeSpan.FromTicks(Math.Clamp(took.Ticks * 4, MinimumCacheLifetime.Ticks, MaximumCacheLifetime.Ticks));
            foreach (var stale in _cache.Where(entry => !Fresh(entry.Value.At, now, entry.Value.Lifetime)).Select(static entry => entry.Key).ToArray()) _cache.Remove(stale);
            if (_cache.Count >= MaximumCachedRoots) _cache.Clear();
            _cache[key] = (now, kept, changes);
            return changes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GitChangeSet?> ReadChangesAsync(string workTree, string? commit, CancellationToken cancellationToken)
    {
        var diffRun = _run!(workTree, ProjectGitChanges.DiffArguments(commit ?? "HEAD"), MaximumListBytes, cancellationToken);
        var untrackedRun = _run(workTree, ProjectGitChanges.UntrackedArguments, MaximumListBytes, cancellationToken);
        var diff = await diffRun.ConfigureAwait(false);
        // A repository without a commit has no HEAD to compare with: everything in it was added.
        if (diff is null && commit is null)
            diff = await _run(workTree, ProjectGitChanges.DiffArguments(ProjectGitChanges.EmptyTree), MaximumListBytes, cancellationToken).ConfigureAwait(false);
        var untracked = await untrackedRun.ConfigureAwait(false);
        if (diff is not { } tracked) return null;

        var truncated = tracked.Truncated || untracked is { Truncated: true };
        var files = ProjectGitChanges.ParseDiff(tracked.Bytes);
        var known = files.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        var counted = 0;
        if (untracked is { } others)
        {
            foreach (var path in ProjectGitChanges.ParseUntracked(others.Bytes))
            {
                if (!known.Add(path)) continue;
                if (files.Count >= ProjectGitChanges.MaximumFiles) { truncated = true; break; }
                var stamp = ProjectGitChanges.Stamp(workTree, path);
                var (lines, binary) = stamp is { } value ? CountUntracked(workTree, path, value, ref counted) : (null, false);
                files.Add(new(path, null, "untracked", lines, lines is null ? null : 0, binary, null, stamp));
            }
        }

        if (files.Count > ProjectGitChanges.MaximumFiles) { files.RemoveRange(ProjectGitChanges.MaximumFiles, files.Count - ProjectGitChanges.MaximumFiles); truncated = true; }
        for (var index = 0; index < files.Count; index++)
            if (files[index] is { Status: not ("untracked" or "deleted") } file) files[index] = file with { Stamp = ProjectGitChanges.Stamp(workTree, file.Path) };
        files.Sort(ComparePaths);
        return new([.. files], files.Sum(static file => file.Insertions ?? 0), files.Sum(static file => file.Deletions ?? 0), truncated);
    }

    // The lines of an untracked file: counted once per content, for a bounded number of new files per list.
    private (int? Lines, bool Binary) CountUntracked(string workTree, string path, (long Length, long WriteTicks) stamp, ref int counted)
    {
        var key = workTree + "\n" + path;
        if (_counted.TryGetValue(key, out var cached) && cached.Length == stamp.Length && cached.WriteTicks == stamp.WriteTicks) return (cached.Lines, cached.Lines is null);
        if (stamp.Length > ProjectGitChanges.MaximumCountedBytes || counted >= ProjectGitChanges.MaximumCountedFiles) return (null, false);
        counted++;
        var (bytes, _) = ProjectGitChanges.ReadWorkTreeFile(workTree, path, ProjectGitChanges.MaximumCountedBytes);
        if (bytes is null) return (null, false);
        var lines = ProjectGitChanges.CountLines(bytes);
        if (_counted.Count >= MaximumCountedCache) _counted.Clear();
        _counted[key] = (stamp.Length, stamp.WriteTicks, lines);
        return (lines, lines is null);
    }

    // The comparisons a page may ask for; a commit is named by its full object id.
    private static bool ValidComparison(string? comparison, string? commit) => comparison switch
    {
        null or HeadComparison or BranchComparison => commit is null,
        CommitComparison => commit is not null && ProjectGitChanges.IsObjectId(commit),
        _ => false,
    };

    // The content of an object of the repository as text, or why it is not shown.
    private async Task<(string? Text, string State)> BlobAsync(Repository repository, string blob, CancellationToken cancellationToken)
    {
        var output = await RunAsync(repository.WorkTree, ["cat-file", "blob", blob], MaximumFileBytes + 1, cancellationToken).ConfigureAwait(false);
        if (output is not { } content) return (null, "unreadable");
        if (content.Truncated || content.Bytes.Length > MaximumFileBytes) return (null, "too_large");
        return ProjectGitChanges.DecodeText(content.Bytes) is { } text ? (text, "text") : (null, "binary");
    }

    // What a commit changed against its first parent (against nothing, for a first commit). Kept for long: it never changes.
    private Task<GitChangeSet?> CommitChangesAsync(Repository repository, string commit, CancellationToken cancellationToken)
        => CachedAsync(repository.WorkTree + "\ncommit\n" + commit, CommitCacheLifetime, async token =>
        {
            var diff = await _run!(repository.WorkTree, ProjectGitChanges.DiffArguments(commit + "^", commit), MaximumListBytes, token).ConfigureAwait(false)
                ?? await _run(repository.WorkTree, ProjectGitChanges.DiffArguments(ProjectGitChanges.EmptyTree, commit), MaximumListBytes, token).ConfigureAwait(false);
            if (diff is not { } output) return null;
            var files = ProjectGitChanges.ParseDiff(output.Bytes);
            var truncated = output.Truncated || files.Count > ProjectGitChanges.MaximumFiles;
            if (files.Count > ProjectGitChanges.MaximumFiles) files.RemoveRange(ProjectGitChanges.MaximumFiles, files.Count - ProjectGitChanges.MaximumFiles);
            files.Sort(ComparePaths);
            return new([.. files], files.Sum(static file => file.Insertions ?? 0), files.Sum(static file => file.Deletions ?? 0), truncated);
        }, cancellationToken);

    private static int ComparePaths(GitChange left, GitChange right)
        => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) is not 0 and var order ? order : string.CompareOrdinal(left.Path, right.Path);

    // Where the branch of a work tree left its base: its upstream, or the main branch when it has none.
    private async Task<BranchBase?> BranchBaseAsync(Repository repository, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = repository.WorkTree + "\n" + repository.Branch;
            if (_bases.TryGetValue(key, out var cached) && Fresh(cached.At, _now!(), BaseCacheLifetime)) return cached.Base;
            BranchBase? value = null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_timeout);
                try
                {
                    value = await ReadBranchBaseAsync(repository, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // No base: only the comparison with the last commit is offered.
                }
            }

            if (_bases.Count >= MaximumCachedRoots) _bases.Clear();
            _bases[key] = (_now!(), value);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BranchBase?> ReadBranchBaseAsync(Repository repository, CancellationToken cancellationToken)
    {
        var reference = Line(await _run!(repository.WorkTree, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], MaximumHeadBytes, cancellationToken).ConfigureAwait(false));
        if (reference is null)
        {
            // A branch that follows no other is compared with the main branch, where there is one.
            var names = await _run(repository.WorkTree, ["for-each-ref", "--format=%(refname:short)", "refs/remotes/origin/main", "refs/remotes/origin/master",
                "refs/heads/main", "refs/heads/master"], MaximumHeadBytes, cancellationToken).ConfigureAwait(false);
            reference = names is { Truncated: false } listed ? Encoding.UTF8.GetString(listed.Bytes).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(name => repository.Detached || !string.Equals(name, repository.Branch, StringComparison.Ordinal)) : null;
        }

        if (reference is not { Length: > 0 and <= MaximumBranchLength } || reference.StartsWith('-') || reference.AsSpan().ContainsAnyInRange('\0', '\u0020')) return null;
        var commit = Line(await _run(repository.WorkTree, ["merge-base", "HEAD", reference], MaximumHeadBytes, cancellationToken).ConfigureAwait(false));
        if (commit is null || !ProjectGitChanges.IsObjectId(commit)) return null;
        var count = Line(await _run(repository.WorkTree, ["rev-list", "--count", commit + "..HEAD"], MaximumHeadBytes, cancellationToken).ConfigureAwait(false));
        return int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var ahead) ? new(reference, commit, ahead) : null;
    }

    // One git run for a file, awaited no longer than the timeout.
    private async Task<GitOutput?> RunAsync(string workTree, IReadOnlyList<string> arguments, int maximumBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await _run!(workTree, arguments, maximumBytes, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Line(GitOutput? output)
        => output is { Truncated: false } value && Encoding.UTF8.GetString(value.Bytes).Trim() is { Length: > 0 } text && !text.AsSpan().ContainsAny('\r', '\n') ? text : null;

    /// <summary>
    /// Finds the repository containing <paramref name="root"/>: the nearest folder at or above it with a
    /// <c>.git</c> folder, or with a <c>.git</c> file naming one (a linked worktree or a submodule).
    /// </summary>
    /// <returns>The work tree and its git directory, or null when the folder is in no repository.</returns>
    internal static (string WorkTree, string GitDirectory)? FindRepository(string root)
    {
        for (var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, ".git");
            if (Directory.Exists(candidate)) return (directory, candidate);
            if (ReadBounded(candidate) is not { } pointer) continue;
            var line = FirstLine(pointer);
            const string prefix = "gitdir:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var target = line[prefix.Length..].Trim();
            if (target.IsEmpty || target.ContainsAnyInRange('\0', '\u001f')) return null;
            var gitDirectory = Path.GetFullPath(target.ToString(), directory);
            return Directory.Exists(gitDirectory) ? (directory, gitDirectory) : null;
        }

        return null;
    }

    /// <summary>Finds the git directory of the repository containing <paramref name="root"/>.</summary>
    /// <returns>The git directory, or null when the folder is in no repository.</returns>
    internal static string? FindGitDirectory(string root) => FindRepository(root)?.GitDirectory;

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
    /// Runs git in a work tree without a shell or any prompt and returns at most <paramref name="maximumBytes"/>
    /// of its output, or null when git is not installed or exits with an error (a repository without a
    /// commit, a folder git does not trust).
    /// </summary>
    /// <exception cref="OperationCanceledException">Canceled; the process and its children were stopped.</exception>
    internal static async Task<GitOutput?> RunGitAsync(string workTree, IReadOnlyList<string> arguments, int maximumBytes, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workTree,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Paths are printed as they are, and a file name never starts a pager or an editor.
        foreach (var argument in (ReadOnlySpan<string>)["-C", workTree, "-c", "core.quotepath=off", "--no-pager"]) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // Never take the index lock for a display.
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
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var room = maximumBytes - (int)output.Length;
                output.Write(buffer, 0, Math.Min(read, room));
                // The rest is not needed: what was read is a list cut short.
                if (read > room) return new(output.ToArray(), true);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? new(output.ToArray(), false) : null;
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
    private static bool Fresh(DateTimeOffset at, DateTimeOffset now, TimeSpan lifetime) => now - at is var age && age >= TimeSpan.Zero && age < lifetime;

    // The repository a request is answered for.
    private sealed record Repository(string ProjectId, string WorkTree, string Prefix, string Branch, bool Detached);

    // The reference a branch is compared with, the commit it left it at, and the commits it made since.
    private sealed record BranchBase(string Reference, string Commit, int Ahead);
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
/// <param name="Insertions">Lines added since <c>HEAD</c>, staged or not, untracked files included; null when git could not answer.</param>
/// <param name="Deletions">Lines removed since <c>HEAD</c>, staged or not; null when git could not answer.</param>
/// <param name="ChangedFiles">Files that differ from <c>HEAD</c>, untracked files included; null when git could not answer.</param>
internal sealed record ProjectGitStatusResponse(string Status, string? ProjectId, string? Branch, bool Detached, int? Insertions, int? Deletions, int? ChangedFiles);

/// <summary>Asks for the changed files of a project's work tree.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required. An archived project is answered too.</param>
/// <param name="Comparison">
/// <c>head</c> (the default): what is not committed. <c>branch</c>: everything since the branch left its base
/// branch, its commits included; answered as <c>head</c> when the branch has no base or no commit of its own.
/// </param>
/// <param name="KnownRevision">The revision of the list the page has; the same list is answered with <c>unchanged</c>.</param>
/// <param name="Commit">
/// With the comparison <c>commit</c>, the full id of the commit whose changes against its parent are listed;
/// unset otherwise.
/// </param>
internal sealed record ProjectGitChangesRequest(string? ExpectedEpoch, string? ProjectId, string? Comparison, string? KnownRevision, string? Commit = null);

/// <summary>
/// <c>ok</c> with the list, <c>unchanged</c> with the project and the revision only, <c>git_failed</c> with the
/// branch only (git is not installed, failed or timed out), or one of the refusals of
/// <see cref="ProjectGitStatusResponse"/> with every other value unset.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project the list belongs to.</param>
/// <param name="Revision">Identifies this answer: it changes when anything in it does, or a file content.</param>
/// <param name="Root">The work tree of the repository, which the paths are relative to.</param>
/// <param name="Prefix">The project folder inside the work tree: empty, or a path ending with a slash.</param>
/// <param name="Branch">The checked-out branch, or the first seven digits of the commit when detached.</param>
/// <param name="Detached">Whether <c>HEAD</c> names a commit instead of a branch.</param>
/// <param name="Comparison">The comparison that was made: <c>head</c>, <c>branch</c> or <c>commit</c>.</param>
/// <param name="BaseReference">The branch the current one is based on (its upstream, or the main branch); null without one.</param>
/// <param name="BaseAhead">The commits of the current branch since it left <paramref name="BaseReference"/>.</param>
/// <param name="Files">The changed files, ordered by path.</param>
/// <param name="Insertions">The sum of the lines added.</param>
/// <param name="Deletions">The sum of the lines removed.</param>
/// <param name="Truncated">There are more changed files than the list holds.</param>
internal sealed record ProjectGitChangesResponse(string Status, string? ProjectId, string? Revision, string? Root, string? Prefix, string? Branch, bool Detached,
    string? Comparison, string? BaseReference, int? BaseAhead, ProjectGitChange[]? Files, int Insertions, int Deletions, bool Truncated)
{
    internal static ProjectGitChangesResponse Refused(string status) => new(status, null, null, null, null, null, false, null, null, null, null, 0, 0, false);
}

/// <summary>One changed file of the list.</summary>
/// <param name="Path">The path relative to the work tree, with forward slashes.</param>
/// <param name="OriginalPath">The path the file had before it was renamed or copied.</param>
/// <param name="Status">
/// <c>modified</c>, <c>added</c>, <c>deleted</c>, <c>renamed</c>, <c>copied</c>, <c>conflicted</c> or <c>untracked</c>.
/// </param>
/// <param name="Insertions">Lines added; null when they are not known (a binary file, a very large new file).</param>
/// <param name="Deletions">Lines removed; null when they are not known.</param>
/// <param name="Binary">The file is no text: it has no diff to show.</param>
/// <param name="Revision">Identifies both contents of the file: a page reads them again when it changes.</param>
internal sealed record ProjectGitChange(string Path, string? OriginalPath, string Status, int? Insertions, int? Deletions, bool Binary, string Revision);

/// <summary>Asks for both contents of one changed file.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Comparison"><c>head</c> (the default) or <c>branch</c>, as for the list.</param>
/// <param name="Path">The path of the file as the list gave it.</param>
/// <param name="Commit">With the comparison <c>commit</c>, the full id of the commit; unset otherwise.</param>
internal sealed record ProjectGitFileRequest(string? ExpectedEpoch, string? ProjectId, string? Comparison, string? Path, string? Commit = null);

/// <summary>Asks for the newest commits of the current branch of a project's repository.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project; required.</param>
/// <param name="Limit">How many commits; 20 when unset, at most 200.</param>
/// <param name="KnownRevision">The revision of the list the page has; the same list is answered with <c>unchanged</c>.</param>
internal sealed record ProjectGitCommitsRequest(string? ExpectedEpoch, string? ProjectId, int? Limit, string? KnownRevision);

/// <summary>
/// <c>ok</c> with the commits (none for a repository without a commit), <c>unchanged</c> with the revision only,
/// or one of the refusals of <see cref="ProjectGitStatusResponse"/>.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project the commits belong to.</param>
/// <param name="Revision">Identifies the list.</param>
/// <param name="Commits">The commits, newest first.</param>
/// <param name="More">There are older commits than the ones listed.</param>
internal sealed record ProjectGitCommitsResponse(string Status, string? ProjectId, string? Revision, ProjectGitCommit[]? Commits, bool More);

/// <summary>
/// <c>ok</c> with the contents, <c>not_changed</c> when the list no longer names the file, <c>git_failed</c>, or
/// one of the refusals of <see cref="ProjectGitStatusResponse"/>.
/// </summary>
/// <param name="Status">The outcome code.</param>
/// <param name="ProjectId">The project the file belongs to.</param>
/// <param name="Path">The path of the file.</param>
/// <param name="Revision">The revision of the contents that were read, comparable with the one of the list.</param>
/// <param name="Original">The content in the commit, when <paramref name="OriginalState"/> is <c>text</c>.</param>
/// <param name="OriginalState">
/// <c>text</c>, <c>absent</c> (the file is new), <c>binary</c>, <c>too_large</c> or <c>unreadable</c>.
/// </param>
/// <param name="Modified">The content in the work tree, when <paramref name="ModifiedState"/> is <c>text</c>.</param>
/// <param name="ModifiedState">
/// <c>text</c>, <c>absent</c> (the file was deleted), <c>binary</c>, <c>too_large</c> or <c>unreadable</c>.
/// </param>
internal sealed record ProjectGitFileResponse(string Status, string? ProjectId, string? Path, string? Revision, string? Original, string? OriginalState,
    string? Modified, string? ModifiedState)
{
    internal static ProjectGitFileResponse Refused(string status) => new(status, null, null, null, null, null, null, null);
}

/// <summary>Asks for the requests to show the changes of a project.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
internal sealed record ProjectGitWatchRequest(string? ExpectedEpoch);

/// <summary>A request of an agent (<c>alta diff show</c>) to show the changes of a project.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Path">The file to select, relative to the work tree; null for the first one.</param>
internal sealed record ProjectGitShowEvent(string ProjectId, string? Path);
