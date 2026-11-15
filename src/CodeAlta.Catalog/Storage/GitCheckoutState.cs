using System.Collections.Immutable;
using System.Globalization;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Storage;

/// <summary>What the git state of a checkout covers.</summary>
public enum GitCheckoutScope
{
    /// <summary>
    /// The project folder: its files, every local branch, the tags, the stashes, the detached HEADs of every worktree and
    /// the submodule repositories of the git directory.
    /// </summary>
    Repository,

    /// <summary>A worktree: its own files and the branch or commit of its HEAD only.</summary>
    Worktree,
}

/// <summary>A local branch with commits that no remote-tracking ref holds.</summary>
/// <param name="Name">The name of the branch, without <c>refs/heads/</c>.</param>
/// <param name="Commits">The commits on no remote-tracking ref.</param>
public sealed record UnpushedBranch(string Name, int Commits);

/// <summary>
/// The facts of a checkout that say whether removing it loses something, read from the local repository only.
/// </summary>
/// <remarks>
/// Nothing contacts a remote: "on a remote" means on a remote-tracking ref as of the last fetch or push. A branch deleted
/// on the remote keeps its remote-tracking ref until a fetch prunes it, so commits that only such a ref holds count as
/// pushed. Commits that only a reflog holds (after a reset or an amend) are not counted: nearly every repository has some,
/// and they would make every checkout hold local work.
/// </remarks>
/// <param name="HasRemote">Whether the repository has a remote; a remote whose URL is a folder inside the checkout does not count.</param>
/// <param name="ChangedFiles">Tracked files with changes, staged or not, conflicts and changed submodules included.</param>
/// <param name="UntrackedFiles">Untracked entries, as git names them (an untracked folder once).</param>
/// <param name="Branch">The branch of HEAD; null when HEAD is detached.</param>
/// <param name="UnpushedBranches">Local branches with commits on no remote-tracking ref; in <see cref="GitCheckoutScope.Worktree"/> scope only the branch of HEAD.</param>
/// <param name="UnpushedBranchesTruncated">Whether some branches were not examined although commits on no remote exist: they may hold some.</param>
/// <param name="Stashes">The stashes of the repository; always 0 in <see cref="GitCheckoutScope.Worktree"/> scope.</param>
/// <param name="DetachedCommits">The commits of its own detached HEAD on no branch and no remote-tracking ref; those of the
/// other worktrees are <see cref="GitCheckoutState.OtherWorktreeDetachedCommits"/>.</param>
/// <param name="SubmodulesWithLocalWork">The paths, relative with <c>/</c>, of the initialized submodules (at any depth) that have local work.</param>
/// <param name="LastCommit">The time of the last commit of HEAD; null when HEAD has no commit yet, or when its time is outside what a date holds.</param>
public sealed record GitCheckoutState(bool HasRemote, int ChangedFiles, int UntrackedFiles, string? Branch, IReadOnlyList<UnpushedBranch> UnpushedBranches,
    bool UnpushedBranchesTruncated, int Stashes, int DetachedCommits, IReadOnlyList<string> SubmodulesWithLocalWork, DateTimeOffset? LastCommit)
{
    /// <summary>
    /// Gets the files of the working tree whose index entry is marked <c>assume-unchanged</c> or <c>skip-worktree</c>:
    /// <c>git status</c> does not show their changes, so each one is counted as possibly changed. The files a sparse
    /// checkout leaves out are not there and do not count.
    /// </summary>
    public int HiddenFiles { get; init; }

    /// <summary>Gets the commits that only tags hold, on no remote-tracking ref; always 0 in <see cref="GitCheckoutScope.Worktree"/> scope.</summary>
    public int TagOnlyCommits { get; init; }

    /// <summary>
    /// Gets the submodule repositories of the git directory that no checked-out submodule uses (a submodule deinitialized,
    /// or left out of a sparse checkout): they are not read, and each one counts as local work. Always 0 in
    /// <see cref="GitCheckoutScope.Worktree"/> scope.
    /// </summary>
    public int UnexaminedSubmoduleRepositories { get; init; }

    /// <summary>Gets the operation in progress in the checkout: <c>merge</c>, <c>rebase</c>, <c>cherry-pick</c>, <c>revert</c> or <c>bisect</c>; null when none.</summary>
    public string? OperationInProgress { get; init; }

    /// <summary>
    /// Gets the commits that only the detached HEADs of the other worktrees of the repository hold, on no branch and no
    /// remote-tracking ref; always 0 in <see cref="GitCheckoutScope.Worktree"/> scope.
    /// </summary>
    public int OtherWorktreeDetachedCommits { get; init; }

    /// <summary>
    /// Gets the local branches not examined one by one because there are more than <see cref="GitCheckoutStateReader.MaximumBranches"/>,
    /// while commits on no remote exist: they may hold some (<see cref="UnpushedBranchesTruncated"/>).
    /// </summary>
    public int UnexaminedBranches { get; init; }

    /// <summary>
    /// Gets the refs outside the branches, the tags, the remote-tracking refs and the stash (notes, a backup of a rewrite,
    /// refs of a tool) that hold commits on no remote-tracking ref, or that were not examined: they go with the repository.
    /// Always 0 in <see cref="GitCheckoutScope.Worktree"/> scope. The copies of the remotes that <c>git maintenance</c>
    /// keeps (<c>refs/prefetch/</c>) do not count.
    /// </summary>
    public int OtherRefs { get; init; }

    /// <summary>Gets the names of the first of <see cref="OtherRefs"/>, at most <see cref="GitCheckoutStateReader.MaximumOtherRefNames"/>.</summary>
    public IReadOnlyList<string> OtherRefNames { get; init; } = [];

    /// <summary>Gets whether HEAD is detached.</summary>
    public bool Detached => Branch is null;

    /// <summary>Gets whether a fact says that removing the checkout loses something; no remote counts.</summary>
    public bool HasLocalWork => !HasRemote || ChangedFiles > 0 || UntrackedFiles > 0 || UnpushedBranches.Count > 0 || UnpushedBranchesTruncated
        || Stashes > 0 || DetachedCommits > 0 || OtherWorktreeDetachedCommits > 0 || SubmodulesWithLocalWork.Count > 0 || HiddenFiles > 0
        || TagOnlyCommits > 0 || UnexaminedSubmoduleRepositories > 0 || OtherRefs > 0 || OperationInProgress is not null;
}

/// <summary>Whether removing a checkout loses something.</summary>
public enum CheckoutVerdict
{
    /// <summary>Git could not be read, or some files could not be: nothing is known.</summary>
    Unknown,

    /// <summary>Everything is on a remote and git ignores nothing but build output: a clone and a build bring the folder back.</summary>
    NothingLocal,

    /// <summary>Everything is on a remote, but ignored files outside build output exist: a clone does not bring them back.</summary>
    IgnoredFilesOnly,

    /// <summary>Removing it loses work: <see cref="GitCheckoutState"/> says what.</summary>
    HasLocalWork,
}

/// <summary>The verdict of a checkout from its git state and its measure.</summary>
public static class CheckoutVerdicts
{
    /// <summary>Decides the verdict of a checkout. When unsure, it is never <see cref="CheckoutVerdict.NothingLocal"/>.</summary>
    /// <param name="state">Its git state; null when git could not be read.</param>
    /// <param name="usage">Its measure; null or with <see cref="CheckoutUsage.GitKnown"/> false when its ignored files are not known.</param>
    /// <returns><see cref="CheckoutVerdict.HasLocalWork"/> when the state says so, else <see cref="CheckoutVerdict.IgnoredFilesOnly"/> when ignored
    /// files outside build output exist, else <see cref="CheckoutVerdict.NothingLocal"/>; <see cref="CheckoutVerdict.Unknown"/> when what decides
    /// is not known: no state, no measure, git not read by the measure, or entries it skipped.</returns>
    public static CheckoutVerdict Of(GitCheckoutState? state, CheckoutUsage? usage)
    {
        if (state is null) return CheckoutVerdict.Unknown;
        if (state.HasLocalWork) return CheckoutVerdict.HasLocalWork;
        // Git exits 0 and only warns when it cannot open a folder: what it skipped may hold anything.
        if (usage is not { GitKnown: true, SkippedEntries: 0 }) return CheckoutVerdict.Unknown;
        return usage.OtherIgnoredCount > 0 ? CheckoutVerdict.IgnoredFilesOnly : CheckoutVerdict.NothingLocal;
    }
}

/// <summary>
/// Reads the <see cref="GitCheckoutState"/> of a checkout with read-only git commands (<c>git remote</c>,
/// <c>git status --porcelain=v2 --branch -z</c>, <c>git ls-files</c>, <c>git for-each-ref</c>, <c>git rev-list</c>,
/// <c>git worktree list</c>) and the marker files of its git directory. Nothing contacts a remote.
/// </summary>
public sealed class GitCheckoutStateReader
{
    /// <summary>The most local branches examined one by one in <see cref="GitCheckoutScope.Repository"/> scope; the most other refs too.</summary>
    public const int MaximumBranches = 100;

    /// <summary>The most names of <see cref="GitCheckoutState.OtherRefs"/> a state carries.</summary>
    public const int MaximumOtherRefNames = 5;

    // The refs that are not other refs: what the other facts count, and the copies of the remotes git maintenance keeps.
    private static readonly ImmutableArray<string> CountedRefPrefixes = ["refs/heads/", "refs/tags/", "refs/remotes/", "refs/prefetch/"];

    // Submodules of submodules: a bound against a repository made to recurse, not a limit met in practice.
    private const int MaximumSubmoduleDepth = 16;

    // The files and folders of a git directory that say an operation waits to be finished, and its name.
    private static readonly ImmutableArray<(string Marker, string Operation)> OperationMarkers =
    [
        ("MERGE_HEAD", "merge"), ("rebase-merge", "rebase"), ("rebase-apply", "rebase"), ("CHERRY_PICK_HEAD", "cherry-pick"),
        ("REVERT_HEAD", "revert"), ("BISECT_LOG", "bisect"),
    ];

    private readonly GitRunner _run;

    /// <summary>Creates a reader over the git on the path, or over <paramref name="run"/>.</summary>
    /// <param name="run">Runs git; null runs <see cref="GitWorktreeService.RunGitAsync(string, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>.</param>
    public GitCheckoutStateReader(GitRunner? run = null) => _run = run ?? GitCheckoutReads.RunAsync;

    /// <summary>Reads the state of a checkout.</summary>
    /// <param name="checkoutRoot">The folder of the checkout: the top of a project folder or of a worktree.</param>
    /// <param name="scope">What the state covers.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The state; null when git could not be read: the folder is not the top of a checkout, git is not installed, a
    /// command failed or its answer was cut, here or in a submodule.</returns>
    /// <exception cref="ArgumentException">The folder is null or empty.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<GitCheckoutState?> ReadAsync(string checkoutRoot, GitCheckoutScope scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        return ReadAsync(Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutRoot)), scope, 0, cancellationToken);
    }

    private async Task<GitCheckoutState?> ReadAsync(string root, GitCheckoutScope scope, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaximumSubmoduleDepth || !Directory.Exists(root) || !await GitCheckoutReads.IsCheckoutRootAsync(_run, root, cancellationToken).ConfigureAwait(false))
            return null;
        if (await Git(root, ["rev-parse", "--show-toplevel"]).ConfigureAwait(false) is not { } topText) return null;
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GitCheckoutReads.Line(topText)));
        var comparer = GitCheckoutReads.PathComparerOf(top);
        if (await GitCheckoutReads.GitDirectoryAsync(_run, root, cancellationToken).ConfigureAwait(false) is not { } gitDirectory) return null;
        if (await HasRemoteAsync(root, top).ConfigureAwait(false) is not { } hasRemote) return null;

        if (await Git(root, ["status", "--porcelain=v2", "--branch", "--untracked-files=normal", "--ignore-submodules=none", "-z"]).ConfigureAwait(false) is not { } status)
            return null;
        string? branch = null;
        var born = false;
        int changed = 0, untracked = 0;
        var records = status.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal)) born = record["# branch.oid ".Length..] != "(initial)";
            else if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
                branch = record["# branch.head ".Length..] is var head && head != "(detached)" ? head : null;
            else if (record.StartsWith("1 ", StringComparison.Ordinal) || record.StartsWith("u ", StringComparison.Ordinal)) changed++;
            else if (record.StartsWith("2 ", StringComparison.Ordinal))
            {
                changed++;
                // A rename or copy record is followed by the path it came from, as a record of its own.
                index++;
            }
            else if (record.StartsWith("? ", StringComparison.Ordinal)) untracked++;
        }

        if (await HiddenFilesAsync(root, string.Empty, 0).ConfigureAwait(false) is not { } hidden) return null;

        DateTimeOffset? lastCommit = null;
        var unpushed = new List<UnpushedBranch>();
        var truncated = false;
        var unexaminedBranches = 0;
        var detachedCommits = 0;
        var otherDetached = 0;
        if (born)
        {
            if (await Git(root, ["log", "-1", "--format=%ct", "HEAD"]).ConfigureAwait(false) is not { } time
                || !long.TryParse(time.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return null;
            // Git keeps times a date cannot hold; such a commit has no time here rather than failing the read.
            if (seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                lastCommit = DateTimeOffset.FromUnixTimeSeconds(seconds);

            if (branch is null)
            {
                if (await Count(root, ["rev-list", "--count", "HEAD", "--not", "--branches", "--remotes", "--"]).ConfigureAwait(false) is not { } count) return null;
                detachedCommits = count;
            }
        }

        if (scope == GitCheckoutScope.Worktree)
        {
            if (born && branch is not null)
            {
                if (await Count(root, ["rev-list", "--count", "refs/heads/" + branch, "--not", "--remotes", "--"]).ConfigureAwait(false) is not { } count) return null;
                if (count > 0) unpushed.Add(new(branch, count));
            }
        }
        else
        {
            // One count for every branch first: most repositories have nothing to look at one by one.
            if (await Count(root, ["rev-list", "--count", "--branches", "--not", "--remotes", "--"]).ConfigureAwait(false) is not { } all) return null;
            if (all > 0)
            {
                if (await Git(root, ["for-each-ref", "--format=%(refname)", "refs/heads"]).ConfigureAwait(false) is not { } heads) return null;
                var names = heads.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                truncated = names.Length > MaximumBranches;
                unexaminedBranches = Math.Max(0, names.Length - MaximumBranches);
                foreach (var reference in names.Take(MaximumBranches))
                {
                    if (await Count(root, ["rev-list", "--count", reference, "--not", "--remotes", "--"]).ConfigureAwait(false) is not { } count) return null;
                    if (count > 0) unpushed.Add(new(reference["refs/heads/".Length..], count));
                }
            }

            if (await OtherDetachedCommitsAsync(root, top).ConfigureAwait(false) is not { } others) return null;
            otherDetached = others;
        }

        int stashes = 0, tagOnly = 0;
        List<string> otherRefNames = [];
        if (scope == GitCheckoutScope.Repository)
        {
            // No stash is not an error: the ref is simply not there.
            if (await Git(root, ["for-each-ref", "--format=%(refname)", "refs/stash"]).ConfigureAwait(false) is not { } stash) return null;
            if (stash.Trim().Length > 0)
            {
                if (await Count(root, ["rev-list", "--walk-reflogs", "--count", "refs/stash", "--"]).ConfigureAwait(false) is not { } count) return null;
                stashes = count;
            }

            if (await Count(root, ["rev-list", "--count", "--tags", "--not", "--remotes", "--"]).ConfigureAwait(false) is not { } tags) return null;
            tagOnly = tags;

            if (await OtherRefsAsync(root).ConfigureAwait(false) is not { } refs) return null;
            otherRefNames = refs;
        }

        if (await GitCheckoutReads.InitializedSubmodulesAsync(_run, root, cancellationToken).ConfigureAwait(false) is not { } submodules) return null;
        var withWork = new List<string>();
        var examined = new HashSet<string>(comparer);
        foreach (var submodule in submodules)
        {
            var inside = Path.Combine(root, submodule.Replace('/', Path.DirectorySeparatorChar));
            if (await GitCheckoutReads.GitDirectoryAsync(_run, inside, cancellationToken).ConfigureAwait(false) is not { } innerGit) return null;
            examined.Add(innerGit);
            // A submodule keeps its branches and stashes in a repository of its own, which goes with the checkout.
            if (await ReadAsync(inside, GitCheckoutScope.Repository, depth + 1, cancellationToken).ConfigureAwait(false) is not { } inner) return null;
            if (!inner.HasLocalWork) continue;
            withWork.Add(submodule);
            withWork.AddRange(inner.SubmodulesWithLocalWork.Select(path => GitCheckoutReads.Prefixed(submodule, path)));
        }

        var unexamined = 0;
        if (scope == GitCheckoutScope.Repository)
        {
            if (UnexaminedModules(Path.Combine(gitDirectory, "modules"), examined, 0) is not { } count) return null;
            unexamined = count;
        }

        return new(hasRemote, changed, untracked, branch, unpushed, truncated, stashes, detachedCommits, withWork, lastCommit)
        {
            HiddenFiles = hidden,
            TagOnlyCommits = tagOnly,
            OtherWorktreeDetachedCommits = otherDetached,
            UnexaminedBranches = unexaminedBranches,
            OtherRefs = otherRefNames.Count,
            OtherRefNames = [.. otherRefNames.Take(MaximumOtherRefNames)],
            UnexaminedSubmoduleRepositories = unexamined,
            OperationInProgress = OperationIn(gitDirectory),
        };

        Task<string?> Git(string folder, IReadOnlyList<string> arguments) => GitCheckoutReads.ReadAsync(_run, folder, arguments, cancellationToken);

        async Task<int?> Count(string folder, IReadOnlyList<string> arguments)
            => await Git(folder, arguments).ConfigureAwait(false) is { } text && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : null;

        // A remote counts unless its URL is a folder inside the checkout, which goes with it.
        async Task<bool?> HasRemoteAsync(string folder, string top)
        {
            if (await Git(folder, ["remote"]).ConfigureAwait(false) is not { } names) return null;
            foreach (var name in names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (await Git(folder, ["remote", "get-url", name]).ConfigureAwait(false) is not { } url) return null;
                if (LocalPath(GitCheckoutReads.Line(url)) is not { } path || (!IsInside(folder, path) && !IsInside(top, path))) return true;
            }

            return false;
        }

        // The other refs that hold commits on no remote-tracking ref, in the order git lists them; past the bound, every one
        // counts unexamined.
        async Task<List<string>?> OtherRefsAsync(string folder)
        {
            if (await Git(folder, ["for-each-ref", "--format=%(refname)"]).ConfigureAwait(false) is not { } listed) return null;
            var found = new List<string>();
            var examined = 0;
            foreach (var reference in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (reference == "refs/stash" || CountedRefPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal))) continue;
                if (examined++ >= MaximumBranches)
                {
                    found.Add(reference);
                    continue;
                }

                if (await Count(folder, ["rev-list", "--count", reference, "--not", "--remotes", "--"]).ConfigureAwait(false) is not { } count) return null;
                if (count > 0) found.Add(reference);
            }

            return found;
        }

        // The commits of the detached HEADs of the other worktrees of the repository that no branch and no remote hold.
        async Task<int?> OtherDetachedCommitsAsync(string folder, string top)
        {
            if (await Git(folder, ["worktree", "list", "--porcelain", "-z"]).ConfigureAwait(false) is not { } list) return null;
            var total = 0;
            string? path = null, head = null;
            var detached = false;
            foreach (var field in list.Split('\0').Append(string.Empty))
            {
                if (field.Length > 0)
                {
                    if (field.StartsWith("worktree ", StringComparison.Ordinal)) path = field["worktree ".Length..];
                    else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = field["HEAD ".Length..];
                    else if (field == "detached") detached = true;
                    continue;
                }

                // An empty field ends a worktree.
                if (path is not null && detached && head is not null
                    && !comparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), top))
                {
                    if (await Count(folder, ["rev-list", "--count", head, "--not", "--branches", "--remotes", "--"]).ConfigureAwait(false) is not { } count) return null;
                    total = checked(total + count);
                }

                (path, head, detached) = (null, null, false);
            }

            return total;
        }

        // The files of the checkout that git status does not look at. Git lists every file of the index, which a large
        // repository answers with more than the runner keeps: a cut answer is asked again folder by folder.
        async Task<int?> HiddenFilesAsync(string folder, string relative, int level)
        {
            string[] all = relative.Length == 0 ? ["ls-files", "-v", "-z"] : ["ls-files", "-v", "-z", "--", ":(top,literal)" + relative + "/"];
            var (output, cut) = await GitCheckoutReads.ReadOrCutAsync(_run, folder, all, cancellationToken).ConfigureAwait(false);
            if (output is not null) return CountHidden(folder, output);
            if (!cut || level > 256) return null;

            // The files right in this folder, then each folder of the working tree: a marked file that is there is in one of
            // them, and the files of folders that are not there are not there either.
            var here = relative.Length == 0 ? "*" : EscapeGlob(relative) + "/*";
            if (await Git(folder, ["ls-files", "-v", "-z", "--", ":(top,glob)" + here]).ConfigureAwait(false) is not { } direct) return null;
            var total = CountHidden(folder, direct);
            string[] subfolders;
            try
            {
                subfolders = [.. Directory.EnumerateDirectories(relative.Length == 0 ? folder : Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)))];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            foreach (var subfolder in subfolders)
            {
                var name = Path.GetFileName(subfolder);
                if (name == ".git" || new DirectoryInfo(subfolder).LinkTarget is not null) continue;
                if (await HiddenFilesAsync(folder, GitCheckoutReads.Prefixed(relative, name), level + 1).ConfigureAwait(false) is not { } count) return null;
                total = checked(total + count);
            }

            return total;
        }
    }

    // Counts the records of `git ls-files -v -z` marked assume-unchanged (a lower-case tag) or skip-worktree (`S`) whose file is there.
    private static int CountHidden(string root, string listing)
    {
        var count = 0;
        foreach (var record in listing.Split('\0'))
        {
            if (record.Length < 3 || record[1] != ' ' || !(char.IsLower(record[0]) || record[0] == 'S')) continue;
            var path = Path.Combine(root, record[2..].Replace('/', Path.DirectorySeparatorChar));
            if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null) count++;
        }

        return count;
    }

    private static string EscapeGlob(string path)
    {
        var text = new System.Text.StringBuilder(path.Length + 8);
        foreach (var character in path)
        {
            if (character is '*' or '?' or '[' or '\\') text.Append('\\');
            text.Append(character);
        }

        return text.ToString();
    }

    // The URL of a remote as a folder of this computer; null for a remote reached through a network.
    private static string? LocalPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.LocalPath : null;
        if (url.Contains("://", StringComparison.Ordinal)) return null;
        if (url.Length >= 3 && char.IsAsciiLetter(url[0]) && url[1] == ':' && url[2] is '/' or '\\') return url;
        // `host:path` is the short form of ssh, unless a slash comes before the colon.
        var colon = url.IndexOf(':');
        var slash = url.IndexOfAny(['/', '\\']);
        return colon >= 0 && (slash < 0 || colon < slash) ? null : url;
    }

    private static bool IsInside(string root, string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, path)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A folder that cannot be told apart from the checkout is not a remote that keeps its work.
            return true;
        }

        // Without case: a remote that may be inside the checkout is not counted as a remote.
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // The repositories under the `modules` folder of a git directory (a name of a submodule may hold slashes) that no
    // examined submodule uses; null when the folder cannot be read. Each one counts once, with what it holds.
    private static int? UnexaminedModules(string folder, HashSet<string> examined, int level)
    {
        if (!Directory.Exists(folder)) return 0;
        if (level > 64) return null;
        try
        {
            var count = 0;
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if (new DirectoryInfo(child).LinkTarget is not null) continue;
                if (File.Exists(Path.Combine(child, "HEAD")) && Directory.Exists(Path.Combine(child, "objects")))
                {
                    if (!examined.Contains(Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)))) count++;
                    continue;
                }

                if (UnexaminedModules(child, examined, level + 1) is not { } inner) return null;
                count += inner;
            }

            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? OperationIn(string gitDirectory)
    {
        foreach (var (marker, operation) in OperationMarkers)
        {
            var path = Path.Combine(gitDirectory, marker);
            if (File.Exists(path) || Directory.Exists(path)) return operation;
        }

        return null;
    }
}
