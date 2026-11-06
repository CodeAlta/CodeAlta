using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Fleet.Identity;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// Removes the worktrees of the sessions of a node that are no longer used (<c>doc/fleet/node.md</c>, "Workspaces"): a
/// headless node runs no CodeAlta Desktop to remove them, and every session started there has one of its own.
/// </summary>
/// <remarks>
/// <para>
/// A pass, once a while after the node starts and then every <see cref="Interval"/>, removes the worktree of a session that is
/// archived, deleted, or idle for <see cref="IdleTime"/>, when it is on an <c>alta/</c> branch, the session neither works nor
/// waits for a person, and nothing in it is uncommitted. Its branch goes with it only when all its commits are in another
/// branch: no work is lost. A session whose worktree was removed gets a new one, from the branch that kept its commits, when
/// it is given a prompt again (<see cref="KeptBranch"/>).
/// </para>
/// <para>
/// The worktrees a pass saw are kept in the fleet folder (<c>fleet/session-worktrees.json</c>): one whose session is no
/// longer listed is the worktree of a deleted session, whose path the deletion no longer gives.
/// </para>
/// </remarks>
public sealed class FleetSessionWorktreeCleanup : IAsyncDisposable
{
    /// <summary>How long a session is idle before its worktree is removed.</summary>
    public static readonly TimeSpan IdleTime = TimeSpan.FromDays(7);

    /// <summary>How long after the start of the node the first pass runs.</summary>
    public static readonly TimeSpan FirstPass = TimeSpan.FromMinutes(2);

    /// <summary>How long between two passes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The name of the file of the worktrees a pass saw, in the fleet folder of the profile.</summary>
    public const string FileName = "session-worktrees.json";

    // The most removed worktrees remembered for the sessions that may come back; the oldest go first.
    private const int MaxRemembered = 2000;

    private readonly CodeAltaHost _host;
    private readonly GitWorktreeService _git;
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Fleet.Worktrees");
    private Task? _loop;
    private FleetWorktreeUsage? _usage;
    private int _disposed;

    /// <summary>Creates the cleanup of the worktrees of the sessions of a node; nothing runs before <see cref="Start"/>.</summary>
    /// <param name="host">The host of the node.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
    public FleetSessionWorktreeCleanup(CodeAltaHost host, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _git = new GitWorktreeService(host.CatalogOptions, new CodeAltaConfigStore(host.CatalogOptions));
        _time = time ?? TimeProvider.System;
        _path = Path.Combine(host.CatalogOptions.FleetRoot, FileName);
    }

    /// <summary>Gets what the last pass measured; null before the first one.</summary>
    public FleetWorktreeUsage? Usage => Volatile.Read(ref _usage);

    /// <summary>Starts the passes: the first after <see cref="FirstPass"/>, then every <see cref="Interval"/>, until disposed.</summary>
    public void Start()
    {
        lock (_gate) _loop ??= Task.Run(LoopAsync);
    }

    /// <summary>Gets the branch that kept the commits of a session whose worktree a pass removed.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The branch; null when none was kept, or no pass removed its worktree.</returns>
    public string? KeptBranch(string sessionId)
    {
        lock (_gate) return Read().Removed.LastOrDefault(removed => string.Equals(removed.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))?.Branch;
    }

    /// <summary>Forgets the removed worktree of a session that works in a new one.</summary>
    /// <param name="sessionId">The session.</param>
    public void Forget(string sessionId)
    {
        lock (_gate)
        {
            var state = Read();
            if (state.Removed.Any(removed => string.Equals(removed.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                Write(state with { Removed = [.. state.Removed.Where(removed => !string.Equals(removed.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))] });
        }
    }

    /// <summary>Runs one pass: removes the worktrees no longer used, and measures the others.</summary>
    /// <param name="cancellationToken">Stops the pass.</param>
    /// <returns>What the worktrees that stay take; the last measure when the sessions cannot be read now.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<FleetWorktreeUsage?> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunPassAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<FleetWorktreeUsage?> RunPassAsync(CancellationToken cancellationToken)
    {
        OwnedWorkspaceSnapshot snapshot;
        try
        {
            snapshot = await _host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OwnedWorkspaceBusyException)
        {
            return Usage;
        }

        var now = _time.GetUtcNow();
        var known = ReadLocked();
        var movedAway = _host.RuntimeService.Relinquishments.ListRelinquished();
        var sessions = snapshot.Sessions.Where(session => !movedAway.Contains(session.SessionId)
            && session.WorktreePath is { Length: > 0 } && session.WorkspacePath is { Length: > 0 }).ToArray();
        var listed = snapshot.Sessions.Select(static session => session.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var atWork = await _host.Commands.ReadSessionsAtWorkAsync([.. sessions.Select(static session => session.SessionId)]).ConfigureAwait(false);
        var waiting = (await _host.ListSessionsWaitingForPersonAsync().ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checkouts = new Dictionary<string, IReadOnlyList<GitWorktree>?>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<FleetSessionWorktreeEntry>();
        var removed = new List<FleetRemovedSessionWorktree>();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = session.WorkspacePath!;
            if (!checkouts.TryGetValue(project, out var worktrees))
                checkouts[project] = worktrees = await _git.ListAsync(project, cancellationToken).ConfigureAwait(false);
            // Only a worktree made for a session (an alta/ branch), never the checkout of the project or a branch of a person.
            if (worktrees is null || GitWorktreeService.Find(worktrees, session.WorktreePath!) is not { Main: false, Missing: false, Branch: { } branch } worktree
                || !branch.StartsWith(GitWorktreeService.BranchPrefix, StringComparison.Ordinal)) continue;
            var unused = session.ViewState?.Archived == true || now - session.UpdatedAt >= IdleTime;
            if (unused && !atWork.Contains(session.SessionId) && !waiting.Contains(session.SessionId)
                && await RemoveAsync(project, worktree.Path, session.SessionId, cancellationToken).ConfigureAwait(false) is { } gone)
            {
                removed.Add(gone with { RemovedAt = now });
                continue;
            }

            kept.Add(new FleetSessionWorktreeEntry(session.SessionId, project, worktree.Path));
        }

        // The worktree of a session that is no longer listed: it was deleted.
        foreach (var entry in known.Worktrees.Where(entry => !listed.Contains(entry.SessionId) && !kept.Any(other => SamePath(other.Root, entry.Root))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(entry.Root)) continue;
            if (await RemoveAsync(entry.ProjectPath, entry.Root, entry.SessionId, cancellationToken).ConfigureAwait(false) is null) kept.Add(entry);
        }

        var usage = new FleetWorktreeUsage(kept.Count, kept.Sum(static entry => Measure(entry.Root)), removed.Count, (int)IdleTime.TotalDays, now);
        lock (_gate)
        {
            // A session that came back meanwhile (Forget) is not remembered again; the deleted ones are not remembered at all.
            var state = Read();
            var remembered = state.Removed.Concat(removed.Where(gone => listed.Contains(gone.SessionId))).TakeLast(MaxRemembered).ToArray();
            Write(new FleetSessionWorktreeState(kept, remembered));
        }

        Volatile.Write(ref _usage, usage);
        return usage;
    }

    // Removes a worktree and the branch whose commits are all elsewhere; null when it stays (changes not committed, git refused).
    private async Task<FleetRemovedSessionWorktree?> RemoveAsync(string project, string root, string sessionId, CancellationToken cancellationToken)
    {
        GitWorktreeOutcome outcome;
        try
        {
            outcome = await _git.RemoveAsync(project, root, force: false, deleteBranch: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (LogManager.IsInitialized) _logger.Warn($"The worktree {root} of the session {sessionId} could not be removed: {exception.Message}");
            return null;
        }

        if (!outcome.Succeeded)
        {
            if (outcome.Status != "dirty" && LogManager.IsInitialized) _logger.Warn($"The worktree {root} of the session {sessionId} stays ({outcome.Status}): {outcome.Message ?? "git said nothing"}");
            return null;
        }

        if (LogManager.IsInitialized)
            _logger.Info($"The worktree {root} of the session {sessionId} was removed" + (outcome.BranchKept is { } branch ? $"; the branch {branch} keeps its commits." : "."));
        return new FleetRemovedSessionWorktree(sessionId, outcome.BranchKept, default);
    }

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(FirstPass, _time, _stop.Token).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    await RunOnceAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (LogManager.IsInitialized) _logger.Warn(exception, "A pass over the worktrees of the sessions failed");
                }

                await Task.Delay(Interval, _time, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The node stops.
        }
    }

    // What the files of a worktree take on the disk, those that can be read; links are not followed.
    private static long Measure(string root)
    {
        try
        {
            return new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }).Sum(static file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool SamePath(string first, string second)
        => string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private FleetSessionWorktreeState ReadLocked()
    {
        lock (_gate) return Read();
    }

    // Under _gate. A file that cannot be read remembers nothing: a worktree it named is found again by its session.
    private FleetSessionWorktreeState Read()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetSessionWorktreeJson.Default.FleetSessionWorktreeState) is { } state
                ? state with { Worktrees = state.Worktrees ?? [], Removed = state.Removed ?? [] }
                : FleetSessionWorktreeState.Empty;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or UnauthorizedAccessException)
        {
            return FleetSessionWorktreeState.Empty;
        }
    }

    // Under _gate.
    private void Write(FleetSessionWorktreeState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(state, FleetSessionWorktreeJson.Default.FleetSessionWorktreeState));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (LogManager.IsInitialized) _logger.Warn($"The worktrees of the sessions could not be recorded: {exception.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task? loop;
        lock (_gate) loop = _loop;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>What the worktrees of the sessions of a node take, as the last pass of their cleanup measured it.</summary>
/// <param name="Count">How many worktrees of sessions there are.</param>
/// <param name="Bytes">What their files take on the disk.</param>
/// <param name="Removed">How many the last pass removed.</param>
/// <param name="IdleDays">How many days a session is idle before its worktree is removed.</param>
/// <param name="MeasuredAt">When it was measured.</param>
public sealed record FleetWorktreeUsage(int Count, long Bytes, int Removed, int IdleDays, DateTimeOffset MeasuredAt);

/// <summary>A worktree of a session a pass saw.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectPath">The folder of its project.</param>
/// <param name="Root">The root of the worktree.</param>
internal sealed record FleetSessionWorktreeEntry(string SessionId, string ProjectPath, string Root);

/// <summary>A worktree a pass removed, for a session that may come back.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Branch">The branch that kept its commits; null when it was deleted with it.</param>
/// <param name="RemovedAt">When it was removed.</param>
internal sealed record FleetRemovedSessionWorktree(string SessionId, string? Branch, DateTimeOffset RemovedAt);

/// <summary>What the cleanup keeps between passes.</summary>
/// <param name="Worktrees">The worktrees of sessions the last pass saw.</param>
/// <param name="Removed">The worktrees removed, the oldest first.</param>
internal sealed record FleetSessionWorktreeState(IReadOnlyList<FleetSessionWorktreeEntry> Worktrees, IReadOnlyList<FleetRemovedSessionWorktree> Removed)
{
    public static FleetSessionWorktreeState Empty { get; } = new([], []);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FleetSessionWorktreeState))]
internal sealed partial class FleetSessionWorktreeJson : JsonSerializerContext;
