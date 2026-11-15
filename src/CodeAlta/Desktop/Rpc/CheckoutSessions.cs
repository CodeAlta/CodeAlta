using CodeAlta.Agent;
using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The checkouts of a repository joined with the sessions of the catalog and the sessions at work: which sessions record
/// each checkout (the worktree a session records, else the folder it was created in), whether a session works in it now,
/// what protects it from removal and when it was last used. The window of the worktrees decides with <see cref="Join"/>;
/// the measure of the disk usage starts from the same join and only adds to it (<see cref="WidenedJoin"/>).
/// </summary>
/// <remarks>
/// <see cref="Join"/> compares paths as they are written, with the rule of the system: what removes a worktree never
/// resolves a link, which could make a checkout look unused or give the sessions of one worktree to another.
/// </remarks>
internal static class CheckoutSessions
{
    /// <summary>The longest session id a checkout names.</summary>
    internal const int MaximumIdLength = 256;

    private const int MaximumPathLength = 4096;
    private const int MaximumTitleLength = 200;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Takes what the sessions at work say about the checkouts: the roots of the checkouts they work in, read from the disk
    /// now, and their ids. Taken before the catalog is read, so that a checkout that changes meanwhile is judged as it was.
    /// </summary>
    /// <param name="working">The sessions at work, each with the folder it works in.</param>
    internal static CheckoutWork AtWork(IReadOnlyList<SessionWorkFolder> working)
        => new(working, BusyCheckouts(working), new HashSet<string>(working.Select(static session => session.SessionId), StringComparer.Ordinal));

    /// <summary>Joins the checkouts of a repository with the sessions that record them and the sessions at work.</summary>
    /// <param name="worktrees">The checkouts git lists, the main one first.</param>
    /// <param name="checkout">The checkout the project lives in; null when it is in none.</param>
    /// <param name="work">The sessions at work, taken with <see cref="AtWork"/>.</param>
    /// <param name="sessions">The sessions of the catalog.</param>
    /// <returns>One entry per checkout, in the order git lists them.</returns>
    internal static CheckoutJoin Join(IReadOnlyList<GitWorktree> worktrees, string? checkout, CheckoutWork work, IReadOnlyList<AgentSessionMetadata> sessions)
    {
        var (busy, running) = (work.Busy, work.Running);

        // The sessions of each checkout, by the folder each records: its worktree, else the folder it was created in.
        var recorded = new Dictionary<string, List<AgentSessionMetadata>>(PathComparer);
        var owners = new Dictionary<string, string?>(PathComparer);
        foreach (var session in sessions)
        {
            var folder = FolderOf(session);
            if (session.SessionId is not { Length: > 0 and <= MaximumIdLength } || !ValidPath(folder)) continue;
            if (!owners.TryGetValue(folder!, out var owner)) owners[folder!] = owner = Owner(worktrees, folder!);
            if (owner is null) continue;
            if (!recorded.TryGetValue(owner, out var list)) recorded[owner] = list = [];
            list.Add(session);
        }

        return new([.. worktrees.Select(worktree =>
        {
            var own = recorded.GetValueOrDefault(worktree.Path) ?? [];
            var inUse = busy.Contains(worktree.Path);
            var project = worktree.Path.Equals(checkout, PathComparison);
            return new JoinedCheckout(worktree, project, inUse, Protection(worktree, checkout, inUse, worktrees), Ordered(own, running), own.Count, LastUse(own));
        })], running);
    }

    /// <summary>
    /// The join of <see cref="Join"/>, widened by the physical paths of the folders: a checkout is in use when either
    /// comparison says so, it is the checkout of the project when either says so, and a session that <see cref="Join"/>
    /// gives to no checkout goes to the one its folder is in physically. Never less than <see cref="Join"/> says: for the
    /// measure of the disk usage, which may protect more than the window of the worktrees, never less.
    /// </summary>
    /// <param name="worktrees">The checkouts git lists, the main one first.</param>
    /// <param name="checkout">The checkout the project lives in; null when it is in none.</param>
    /// <param name="work">The sessions at work, taken with <see cref="AtWork"/>.</param>
    /// <param name="sessions">The sessions of the catalog.</param>
    /// <returns>One entry per checkout, in the order git lists them.</returns>
    internal static CheckoutJoin WidenedJoin(IReadOnlyList<GitWorktree> worktrees, string? checkout, CheckoutWork work, IReadOnlyList<AgentSessionMetadata> sessions)
    {
        var join = Join(worktrees, checkout, work, sessions);
        var working = work.Sessions;
        // Each checkout with its physical path, and the case rule of its own volume.
        var physical = worktrees.Select(static worktree => (Worktree: worktree, Path: CheckoutPaths.Physical(worktree.Path), Comparer: CheckoutPaths.ComparerOf(worktree.Path)))
            .ToArray();
        var project = checkout is null ? null : CheckoutPaths.Physical(checkout);
        var busy = working.Where(static session => ValidPath(session.Folder))
            .Select(static session => GitWorktreeService.FindCheckoutRoot(CheckoutPaths.Physical(session.Folder))).OfType<string>().ToArray();

        // The sessions the join gives to none, by the checkout their folder is in physically.
        var given = new HashSet<string>(join.Checkouts.SelectMany(static joined => joined.Sessions).Select(static session => session.SessionId), StringComparer.Ordinal);
        var added = new Dictionary<GitWorktree, List<AgentSessionMetadata>>(ReferenceEqualityComparer.Instance);
        foreach (var session in sessions)
        {
            var folder = FolderOf(session);
            if (session.SessionId is not { Length: > 0 and <= MaximumIdLength } || !ValidPath(folder) || given.Contains(session.SessionId)) continue;
            if (PhysicalOwner(physical, CheckoutPaths.Physical(folder!)) is not { } owner) continue;
            if (!added.TryGetValue(owner, out var list)) added[owner] = list = [];
            list.Add(session);
        }

        return new([.. join.Checkouts.Select((joined, index) =>
        {
            var (worktree, path, comparer) = physical[index];
            var inUse = joined.InUse || busy.Any(root => comparer.Equals(root, path));
            var isProject = joined.Project || (project is not null && comparer.Equals(project, path));
            IReadOnlyList<AgentSessionMetadata> own = [.. joined.Sessions, .. added.GetValueOrDefault(worktree) ?? []];
            return new JoinedCheckout(worktree, isProject, inUse, Protection(worktree, isProject, inUse, worktrees), Ordered(own, join.Running), own.Count, LastUse(own));
        })], join.Running);
    }

    /// <summary>The roots of the checkouts a session is at work in. A session works in the nearest checkout that holds its folder.</summary>
    /// <param name="working">The sessions at work.</param>
    internal static HashSet<string> BusyCheckouts(IReadOnlyList<SessionWorkFolder> working)
    {
        var checkouts = new HashSet<string>(PathComparer);
        foreach (var session in working)
        {
            if (GitWorktreeService.FindCheckoutRoot(session.Folder) is { } checkout) checkouts.Add(checkout);
        }

        return checkouts;
    }

    /// <summary>The checkouts the sessions of the catalog work in, by the folder each records, as the join counts them.</summary>
    /// <param name="worktrees">The checkouts git lists.</param>
    /// <param name="sessions">The sessions of the catalog.</param>
    internal static HashSet<string> UsedCheckouts(IReadOnlyList<GitWorktree> worktrees, IReadOnlyList<AgentSessionMetadata> sessions)
    {
        var used = new HashSet<string>(PathComparer);
        foreach (var session in sessions)
        {
            var folder = FolderOf(session);
            if (ValidPath(folder) && Owner(worktrees, folder!) is { } owner) used.Add(owner);
        }

        return used;
    }

    /// <summary>
    /// Why a checkout is not removed: it is the one of the project or the main one of the repository (<c>main</c>), a
    /// session is at work in it (<c>in_use</c>), git keeps it (<c>locked</c>), or git lists another worktree of the
    /// repository inside its folder, which removing it would delete (<c>holds_worktree</c>). Null for a worktree that can go.
    /// </summary>
    /// <param name="worktree">The checkout.</param>
    /// <param name="checkout">The checkout the project lives in.</param>
    /// <param name="busy">Whether a session is at work in it.</param>
    /// <param name="worktrees">The checkouts git lists for the repository.</param>
    internal static string? Protection(GitWorktree worktree, string? checkout, bool busy, IReadOnlyList<GitWorktree> worktrees)
        => Protection(worktree, worktree.Path.Equals(checkout, PathComparison), busy, worktrees);

    /// <summary>Whether git lists another worktree of the repository, whose folder is there, inside the folder of a checkout.</summary>
    /// <param name="worktree">The checkout.</param>
    /// <param name="worktrees">The checkouts git lists for the repository.</param>
    internal static bool HoldsWorktree(GitWorktree worktree, IReadOnlyList<GitWorktree> worktrees)
        => !worktree.Missing && worktrees.Any(other => !other.Missing && !other.Path.Equals(worktree.Path, PathComparison) && GitWorktreeService.IsWithin(other.Path, worktree.Path));

    private static string? Protection(GitWorktree worktree, bool project, bool busy, IReadOnlyList<GitWorktree> worktrees)
        => worktree.Main || project ? "main" : busy ? "in_use" : worktree.Locked ? "locked" : HoldsWorktree(worktree, worktrees) ? "holds_worktree" : null;

    /// <summary>The title a session is listed with, on one line and short enough for a row.</summary>
    internal static string Title(AgentSessionMetadata session)
    {
        var title = SessionRuntimeService.ListedTitle(session, null, null);
        var text = new System.Text.StringBuilder(Math.Min(title.Length, MaximumTitleLength));
        for (var index = 0; index < title.Length && text.Length < MaximumTitleLength; index++)
        {
            var character = title[index];
            if (char.IsHighSurrogate(character) && index + 1 < title.Length && char.IsLowSurrogate(title[index + 1]))
            {
                if (text.Length + 2 > MaximumTitleLength) break;
                text.Append(character).Append(title[++index]);
            }
            else if (!char.IsSurrogate(character)) text.Append(char.IsControl(character) ? ' ' : character);
        }

        return text.ToString().Trim() is { Length: > 0 } shown ? shown : session.SessionId;
    }

    /// <summary>Whether a path is one a request or a session may name: absolute, bounded, without control characters.</summary>
    internal static bool ValidPath(string? path)
    {
        if (path is not { Length: > 0 and <= MaximumPathLength } || path.AsSpan().ContainsAnyInRange('\0', '\u001f')) return false;
        try
        {
            // A path that cannot be made whole names no folder.
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // The sessions of a checkout: the ones at work first, then the most recent.
    private static IOrderedEnumerable<AgentSessionMetadata> Ordered(IReadOnlyList<AgentSessionMetadata> own, IReadOnlySet<string> running)
        => own.OrderByDescending(session => running.Contains(session.SessionId)).ThenByDescending(static session => session.UpdatedAt)
            .ThenBy(static session => session.SessionId, StringComparer.Ordinal);

    private static DateTimeOffset? LastUse(IReadOnlyList<AgentSessionMetadata> own) => own.Count == 0 ? null : own.Max(static session => session.UpdatedAt);

    // The folder a session records: its worktree, else the folder it was created in.
    private static string? FolderOf(AgentSessionMetadata session)
        => string.IsNullOrWhiteSpace(session.WorktreePath) ? session.WorkspacePath : session.WorktreePath;

    // The checkout a folder that a session records is in. A folder that is there is in the nearest checkout above
    // it; one that is gone is only in a worktree git still lists without its folder, never in the checkout around it.
    private static string? Owner(IReadOnlyList<GitWorktree> worktrees, string folder)
    {
        if (GitWorktreeService.Find(worktrees, folder) is not { } holder) return null;
        if (holder.Missing) return holder.Path;
        return Directory.Exists(folder) && GitWorktreeService.FindCheckoutRoot(folder) is { } at && at.Equals(holder.Path, PathComparison) ? holder.Path : null;
    }

    // The checkout a folder is physically in: the nearest one that is there and holds it, each compared with the case rule
    // of its own volume. Only for a session the join gives to none.
    private static GitWorktree? PhysicalOwner(IReadOnlyList<(GitWorktree Worktree, string Path, StringComparer Comparer)> worktrees, string folder)
    {
        if (!Directory.Exists(folder) || GitWorktreeService.FindCheckoutRoot(folder) is not { } at) return null;
        var holders = worktrees.Where(candidate => !candidate.Worktree.Missing && candidate.Comparer.Equals(at, candidate.Path)).ToArray();
        // A folder two checkouts reach (one is a link to the other) is given to neither.
        return holders.Length == 1 ? holders[0].Worktree : null;
    }
}

/// <summary>What the sessions at work say about the checkouts, taken at one moment.</summary>
/// <param name="Sessions">The sessions at work, each with the folder it works in.</param>
/// <param name="Busy">The roots of the checkouts they work in, as the disk was then.</param>
/// <param name="Running">Their ids.</param>
internal sealed record CheckoutWork(IReadOnlyList<SessionWorkFolder> Sessions, IReadOnlySet<string> Busy, IReadOnlySet<string> Running);

/// <summary>The checkouts of a repository joined with their sessions.</summary>
/// <param name="Checkouts">One entry per checkout, in the order git lists them.</param>
/// <param name="Running">The ids of the sessions at work.</param>
internal sealed record CheckoutJoin(IReadOnlyList<JoinedCheckout> Checkouts, IReadOnlySet<string> Running);

/// <summary>A checkout with the sessions that record it.</summary>
/// <param name="Worktree">The checkout, as git lists it.</param>
/// <param name="Project">Whether it is the checkout the project lives in.</param>
/// <param name="InUse">Whether a session is at work in it right now.</param>
/// <param name="Protection">Why it is not removed: <c>main</c>, <c>in_use</c> or <c>locked</c>; null for a worktree that can go.</param>
/// <param name="Sessions">The sessions of the catalog that record it: the ones at work first, then the most recent, sorted as they are read.</param>
/// <param name="SessionCount">How many sessions of the catalog record it.</param>
/// <param name="LastUsedAt">When the most recent of those sessions was last updated; null when none records it.</param>
internal sealed record JoinedCheckout(GitWorktree Worktree, bool Project, bool InUse, string? Protection, IEnumerable<AgentSessionMetadata> Sessions,
    int SessionCount, DateTimeOffset? LastUsedAt);
