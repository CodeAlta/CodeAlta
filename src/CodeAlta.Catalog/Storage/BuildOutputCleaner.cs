using System.Globalization;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.Storage;

/// <summary>What a clean did with one path the user confirmed.</summary>
public enum CleanOutcome
{
    /// <summary>It is gone, and its parent could be read to say so.</summary>
    Removed,

    /// <summary>Something of it stayed: a nested repository git keeps, a file it could not remove.</summary>
    PartlyRemoved,

    /// <summary>Kept: the new measure found a repository in it.</summary>
    KeptHoldsRepository,

    /// <summary>Kept: a folder above it, below the checkout, is a link.</summary>
    KeptBelowLink,

    /// <summary>Kept: the new measure does not list it as build output (any more).</summary>
    KeptNotBuildOutput,

    /// <summary>Not attempted: git failed on an earlier run, the clean was canceled or refused before its run.</summary>
    NotAttempted,

    /// <summary>Git was asked to remove it, but it or its parent could not be read afterwards: nothing is said removed.</summary>
    Unknown,
}

/// <summary>What a clean did with one path the user confirmed.</summary>
/// <param name="Path">The path relative to the checkout, with <c>/</c>, as <see cref="IgnoredEntry.Path"/>.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="BytesBefore">What it held when the clean measured it; 0 when the measure does not list it.</param>
/// <param name="BytesFreed">What it holds less after the clean; 0 unless <see cref="CleanOutcome.Removed"/> or <see cref="CleanOutcome.PartlyRemoved"/>.</param>
public sealed record CleanedEntry(string Path, CleanOutcome Outcome, long BytesBefore, long BytesFreed);

/// <summary>What a clean of the build output of a checkout did.</summary>
/// <param name="Status"><see cref="BuildOutputCleaner.Ok"/>, <see cref="BuildOutputCleaner.Canceled"/>, <see cref="BuildOutputCleaner.Failed"/>,
/// <see cref="BuildOutputCleaner.CheckoutChanged"/>, <see cref="BuildOutputCleaner.NotRepository"/>, <see cref="BuildOutputCleaner.GitTooOld"/>, or the
/// status of a <see cref="CleanStop"/> of the guard of the caller.</param>
/// <param name="Message">What git said when it failed, on one line and bounded, why the clean was refused, or what the guard said; null otherwise.</param>
/// <param name="Entries">One entry for each confirmed path, in their order, whatever the status.</param>
/// <param name="BytesFreed">The sum of what the entries hold less.</param>
/// <param name="MoreRemain">Whether the measure listed fewer ignored entries than there are: build folders it did not list were not cleaned.</param>
public sealed record BuildOutputCleanup(string Status, string? Message, IReadOnlyList<CleanedEntry> Entries, long BytesFreed, bool MoreRemain);

/// <summary>What the caller of a clean knows now, and how it follows the clean.</summary>
/// <param name="ExcludedFolders">
/// The folders inside the checkout that the caller measures on their own now (another checkout, a project folder, the
/// profile), as full paths: the new measure leaves them out, and a build folder that holds one is never cleaned.
/// </param>
public sealed record BuildOutputCleanOptions(IReadOnlyCollection<string> ExcludedFolders)
{
    /// <summary>
    /// Gets what is asked right before each run of git: null to go on, or why the clean stops there; the entries that
    /// remain are then <see cref="CleanOutcome.NotAttempted"/> and the status is the one it gives.
    /// </summary>
    public Func<CleanStop?>? Guard { get; init; }

    /// <summary>Gets where how far the clean is goes: at its start, then after each run of git.</summary>
    public IProgress<CleanProgress>? Progress { get; init; }
}

/// <summary>Why the guard of a caller stops a clean.</summary>
/// <param name="Status">The status the clean ends with, such as <c>busy</c>.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record CleanStop(string Status, string Message);

/// <summary>How far a clean is.</summary>
/// <param name="Done">The paths of the runs of git done, or skipped, so far.</param>
/// <param name="Total">The paths that go to git.</param>
public sealed record CleanProgress(int Done, int Total);

/// <summary>
/// Removes the build folders of a checkout the user confirmed, with <c>git clean -f -d -X</c> in the repository each belongs
/// to, bound to the git directory and the physical working tree a new measure read. Only a confirmed path that the new
/// measure also calls build output goes (<see cref="Planned"/>), and git removes only what it ignores. Git keeps an intact
/// nested repository that is not bare; the measure keeps every folder that holds anything of a repository out of the plan.
/// When unsure, nothing is deleted.
/// </summary>
/// <remarks>Who may clean a checkout (a session at work in it, the application running from it) is decided by the caller.</remarks>
public sealed class BuildOutputCleaner
{
    /// <summary>The status of a clean whose runs of git all ended well; each entry says what happened to it.</summary>
    public const string Ok = "ok";

    /// <summary>The status of a clean canceled between two runs of git: the later entries were not attempted.</summary>
    public const string Canceled = "canceled";

    /// <summary>The status when a run of git failed; <see cref="BuildOutputCleanup.Message"/> says what.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The status when a repository is not the one the user saw (another physical root, git directory or folder, for the
    /// checkout or a submodule), found before git runs or right before one of its runs: the entries not cleaned yet are not attempted.
    /// </summary>
    public const string CheckoutChanged = "checkout_changed";

    /// <summary>The status when the folder is not a checkout git can read: nothing is deleted.</summary>
    public const string NotRepository = "not_repository";

    /// <summary>The status on Windows when git is older than 2.23, whose <c>git clean</c> followed junctions: nothing is deleted.</summary>
    public const string GitTooOld = "git_too_old";

    // Paths of one git run: a command line stays far below the limits of every system (32767 characters on Windows).
    private const int MaximumBatchPaths = 64;
    private const int MaximumBatchCharacters = 8000;

    private readonly GitRunner _run;
    private readonly CheckoutUsageScanner _scanner;

    /// <summary>Creates a cleaner over the git on the path, or over <paramref name="run"/>.</summary>
    /// <param name="run">Runs git; null runs <see cref="GitWorktreeService.RunGitAsync(string, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>.</param>
    public BuildOutputCleaner(GitRunner? run = null)
    {
        _run = run ?? GitWorktreeService.RunGitAsync;
        _scanner = new CheckoutUsageScanner(run);
    }

    // Git for Windows before 2.23 followed junctions in `git clean`; the tests ask for the check elsewhere too.
    internal bool RequiresJunctionSafeGit { get; init; } = OperatingSystem.IsWindows();

    /// <summary>
    /// Gets what a clean of the checkout of a measure would remove, for the user to confirm: its ignored entries that are
    /// build output and folders, none of which holds a repository. Files, the files of the system included, stay.
    /// </summary>
    /// <param name="usage">The measure.</param>
    /// <returns>The entries, in the order of the measure; empty when git was not read.</returns>
    /// <exception cref="ArgumentNullException">The measure is null.</exception>
    public static IReadOnlyList<IgnoredEntry> Planned(CheckoutUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.GitKnown ? [.. usage.IgnoredEntries.Where(static entry => entry is { BuildOutput: true, Folder: true, HoldsRepository: false })] : [];
    }

    /// <summary>
    /// Measures the checkout of a confirmed measure again and removes the confirmed paths that both the confirmed measure
    /// and the new one plan (<see cref="Planned"/>); a planned path that was not confirmed, or that the confirmed measure did
    /// not plan, is not touched. The confirmation holds for the repositories that were shown: when the new measure finds
    /// another physical root, git directory or folder (<see cref="CheckoutRepository.WorkTreeIdentity"/>) for the checkout or
    /// a submodule, nothing of it is cleaned. Right before each run of git, the guard of the caller is asked, its repository
    /// is checked again, and each of its folders is looked into again for a repository. Cancellation is honored before git
    /// starts and between its runs, never during one.
    /// </summary>
    /// <param name="confirmedMeasure">The measure the user saw and confirmed paths of.</param>
    /// <param name="confirmedPaths">The paths the user confirmed, exactly as <see cref="IgnoredEntry.Path"/> of that measure gives them.</param>
    /// <param name="options">The folders measured on their own as the caller knows them now, its guard and where to report how far the clean is.</param>
    /// <param name="cancellationToken">Stops the clean before git runs, or between its runs with the status <see cref="Canceled"/>.</param>
    /// <returns>An entry for each confirmed path; a failure of git is a status, not an exception.</returns>
    /// <exception cref="ArgumentNullException">The measure or the confirmed paths are null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled before anything was deleted.</exception>
    public async Task<BuildOutputCleanup> CleanAsync(CheckoutUsage confirmedMeasure, IReadOnlyCollection<string> confirmedPaths, BuildOutputCleanOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmedMeasure);
        ArgumentNullException.ThrowIfNull(confirmedPaths);
        ArgumentNullException.ThrowIfNull(options);
        // The paths come from our own measure: they match exactly, in their case too.
        var confirmed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in confirmedPaths)
        {
            if (path?.Trim('/') is { Length: > 0 } trimmed && seen.Add(trimmed)) confirmed.Add(trimmed);
        }

        BuildOutputCleanup Refused(string status, string? message)
            => new(status, message, [.. confirmed.Select(static path => new CleanedEntry(path, CleanOutcome.NotAttempted, 0, 0))], 0, false);

        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmedMeasure.GitKnown || confirmedMeasure.Repositories.Count == 0) return Refused(NotRepository, null);
        var checkoutRoot = confirmedMeasure.RootPath;
        if (RequiresJunctionSafeGit)
        {
            var version = await _run(checkoutRoot, ["version"], GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (!version.Succeeded || !IsJunctionSafe(version.Output))
                return Refused(GitTooOld, "Cleaning needs Git for Windows 2.23 or later, whose git clean does not follow junctions; found: "
                    + (GitWorktreeService.Clean(version.Output) ?? "no git") + ".");
        }

        // Never a list from the caller alone: what goes is also what git ignores now, in the checkout that was shown.
        // Without the folders the caller measures on its own now: what is measured on its own is never cleaned with this checkout.
        var usage = await _scanner.ScanAsync(checkoutRoot, options.ExcludedFolders, null, cancellationToken).ConfigureAwait(false);
        if (!usage.GitKnown || usage.Repositories.Count == 0) return Refused(NotRepository, null);
        var (shown, now) = (confirmedMeasure.Repositories[0], usage.Repositories[0]);
        if (!SameRepository(shown, now))
            return Refused(CheckoutChanged, $"The checkout changed since it was measured: it was {shown.WorkTree} with {shown.GitDirectory}, "
                + $"it is {now.WorkTree} with {now.GitDirectory}{(SamePaths(shown, now) ? ", another folder at the same paths" : string.Empty)}.");
        var physical = now.WorkTree;
        string Full(string relative) => Path.Combine(physical, relative.Replace('/', Path.DirectorySeparatorChar));

        var planned = Planned(usage).ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        // What the user saw planned: a folder that became build output since is not one they confirmed.
        var shownPlanned = Planned(confirmedMeasure).Select(static entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var listed = new Dictionary<string, IgnoredEntry>(StringComparer.Ordinal);
        foreach (var entry in usage.IgnoredEntries) listed.TryAdd(entry.Path, entry);
        var outcomes = new Dictionary<string, CleanedEntry>(StringComparer.Ordinal);
        var toClean = new List<IgnoredEntry>();
        foreach (var path in confirmed)
        {
            if (shownPlanned.Contains(path) && planned.TryGetValue(path, out var entry))
            {
                if (IsBelowALink(physical, entry.Path)) outcomes[path] = Kept(entry, CleanOutcome.KeptBelowLink);
                else toClean.Add(entry);
            }
            else if (listed.TryGetValue(path, out var other))
                outcomes[path] = Kept(other, other.HoldsRepository ? CleanOutcome.KeptHoldsRepository : CleanOutcome.KeptNotBuildOutput);
            else
                outcomes[path] = new(path, CleanOutcome.KeptNotBuildOutput, 0, 0);
        }

        // Each repository is still the one the user saw: the same paths and folders as in the confirmed measure, the top
        // of its checkout, reached through no link, with the same git directory. Checked for all of them before anything is deleted.
        var runs = new List<(CheckoutRepository Repository, List<IgnoredEntry> Batch)>();
        foreach (var group in toClean.GroupBy(static entry => entry.Repository, StringComparer.Ordinal))
        {
            var repository = confirmedMeasure.Repositories.FirstOrDefault(candidate => candidate.Path == group.Key);
            var current = usage.Repositories.FirstOrDefault(candidate => candidate.Path == group.Key);
            if (repository is null || current is null || !SameRepository(repository, current) || !await IsStillAsync(repository, physical, cancellationToken).ConfigureAwait(false))
            {
                foreach (var entry in toClean) outcomes[entry.Path] = Kept(entry, CleanOutcome.NotAttempted);
                return Result(CheckoutChanged, $"The repository '{(group.Key.Length == 0 ? "." : group.Key)}' changed since it was measured.", 0);
            }

            runs.AddRange(Batches(group.Key, [.. group]).Select(batch => (repository, batch)));
        }

        // Without case whatever the volume: a match keeps a folder.
        var gitDirectories = new HashSet<string>(usage.Repositories.SelectMany(static repository => new[] { repository.GitDirectory, repository.CommonDirectory }),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in toClean) outcomes[entry.Path] = Kept(entry, CleanOutcome.NotAttempted);
        var attempted = new List<IgnoredEntry>();
        var started = false;
        string? failure = null;
        var canceled = false;
        string? stopStatus = null;
        var (done, total) = (0, toClean.Count);
        options.Progress?.Report(new(done, total));
        foreach (var (repository, scheduled) in runs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Before the first run nothing is deleted: the caller hears of the cancellation as usual.
                if (!started) cancellationToken.ThrowIfCancellationRequested();
                canceled = true;
                break;
            }

            // The caller may have a reason to stop now (a session started to work in the checkout).
            if (options.Guard?.Invoke() is { } stop)
            {
                (stopStatus, failure) = (stop.Status, stop.Message);
                break;
            }

            // Checked again right before the run: the repository may have been replaced since the checks above (a folder
            // recreated, the checkout replaced by a link to another repository).
            if (!await IsStillAsync(repository, physical, CancellationToken.None).ConfigureAwait(false))
            {
                (stopStatus, failure) = (CheckoutChanged, $"The repository '{(repository.Path.Length == 0 ? "." : repository.Path)}' changed since it was measured.");
                break;
            }

            // An earlier run took time: a repository may have appeared in a folder since the measure. The look stops at
            // the first one, and a look that could not read everything is no proof that there is none.
            var batch = new List<IgnoredEntry>(scheduled.Count);
            foreach (var entry in scheduled)
            {
                var look = FolderUsage.FindRepository(Full(entry.Path), gitDirectories, CancellationToken.None);
                if (look.HoldsRepository) outcomes[entry.Path] = Kept(entry, CleanOutcome.KeptHoldsRepository);
                else if (look.Skipped > 0) outcomes[entry.Path] = Kept(entry, CleanOutcome.KeptNotBuildOutput);
                else batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                done += scheduled.Count;
                options.Progress?.Report(new(done, total));
                continue;
            }

            started = true;
            // The git directory and the physical working tree the measure read, before `clean`: a core.worktree changed
            // since moves nothing elsewhere. Each path is literal and from the top: no pattern, no magic, never read as an
            // option. Guaranteed: right before this command, the working tree and the git directory were the folders the
            // user saw (their identities), the top of a checkout, reached through no link from the checkout. Not
            // guaranteed: what changes in the milliseconds between that check and git reading its paths (a folder replaced
            // by a link then is not seen); git itself does not follow a link inside the working tree.
            var arguments = new List<string>(batch.Count + 7)
            {
                "--git-dir=" + repository.GitDirectory, "--work-tree=" + repository.WorkTree, "clean", "-f", "-d", "-X", "--",
            };
            arguments.AddRange(batch.Select(entry => ":(top,literal)" + InRepository(repository.Path, entry.Path)));
            attempted.AddRange(batch);
            GitRun run;
            try
            {
                // Not the caller's token: git is not stopped in the middle of a removal.
                run = await _run(repository.WorkTree, arguments, GitWorktreeService.WriteTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A runner that throws after deleting must not lose what the earlier runs did.
                run = new(GitRun.NotStarted, string.Empty, exception.Message);
            }

            done += scheduled.Count;
            options.Progress?.Report(new(done, total));
            if (run.Succeeded) continue;
            failure = GitWorktreeService.Clean(run.Error.Length > 0 ? run.Error : run.Output) ?? $"git clean ended with code {run.ExitCode}.";
            break;
        }

        long freed = 0;
        foreach (var entry in attempted)
        {
            var (outcome, less) = Evaluate(Full(entry.Path), entry.Bytes);
            if (!GitCheckoutReads.TryAdd(ref freed, less)) less = 0;
            outcomes[entry.Path] = new(entry.Path, outcome, entry.Bytes, less);
        }

        return Result(stopStatus ?? (failure is not null ? Failed : canceled ? Canceled : Ok), failure, freed);

        BuildOutputCleanup Result(string status, string? message, long bytesFreed)
            => new(status, message, [.. confirmed.Select(path => outcomes[path])], bytesFreed, usage.IgnoredEntriesTruncated);

        static CleanedEntry Kept(IgnoredEntry entry, CleanOutcome outcome) => new(entry.Path, outcome, entry.Bytes, 0);
    }

    // The same repository as the one the user saw: the same paths, and the same folders when the measure could read them.
    private static bool SameRepository(CheckoutRepository shown, CheckoutRepository now)
        => SamePaths(shown, now) && shown.WorkTreeIdentity is { } workTree && shown.GitDirectoryIdentity is { } gitDirectory
           && workTree == now.WorkTreeIdentity && gitDirectory == now.GitDirectoryIdentity;

    private static bool SamePaths(CheckoutRepository shown, CheckoutRepository now)
        => string.Equals(shown.Path, now.Path, StringComparison.Ordinal) && string.Equals(shown.WorkTree, now.WorkTree, StringComparison.Ordinal)
           && string.Equals(shown.GitDirectory, now.GitDirectory, StringComparison.Ordinal);

    // Whether a repository the user saw is still there as they saw it: its folders, the top of a checkout, reached through
    // no link from the checkout, with the same git directory.
    private async Task<bool> IsStillAsync(CheckoutRepository repository, string physical, CancellationToken cancellationToken)
        => repository.WorkTreeIdentity is { } workTree && FileIdentity.Of(repository.WorkTree) == workTree
           && repository.GitDirectoryIdentity is { } gitDirectory && FileIdentity.Of(repository.GitDirectory) == gitDirectory
           && !GitCheckoutReads.IsReachedThroughLink(physical, repository.Path)
           && await GitCheckoutReads.IsCheckoutRootAsync(_run, repository.WorkTree, cancellationToken).ConfigureAwait(false)
           && await GitCheckoutReads.GitDirectoryAsync(_run, repository.WorkTree, cancellationToken).ConfigureAwait(false) is { } now
           && string.Equals(now, repository.GitDirectory, StringComparison.Ordinal);

    /// <summary>Gets whether the answer of <c>git version</c> names a git whose <c>git clean</c> does not follow junctions: 2.23 or later.</summary>
    /// <param name="versionOutput">What <c>git version</c> printed, such as <c>git version 2.45.1.windows.1</c>.</param>
    /// <returns>Whether it is 2.23 or later; false when it cannot be read.</returns>
    internal static bool IsJunctionSafe(string versionOutput)
    {
        const string prefix = "git version ";
        var text = versionOutput.Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = text[prefix.Length..].Split('.');
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && (major > 2 || (major == 2 && minor >= 23));
    }

    // What git left of an entry. Gone counts as removed only when its parent can be read; what cannot be read is unknown
    // and frees nothing. Never throws: the report of what was deleted must reach the caller.
    private static (CleanOutcome Outcome, long BytesFreed) Evaluate(string full, long before)
    {
        try
        {
            try
            {
                File.GetAttributes(full);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                var parent = Path.GetDirectoryName(full)!;
                File.GetAttributes(parent);
                using var listing = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
                listing.MoveNext();
                return (CleanOutcome.Removed, before);
            }

            var after = FolderUsage.Measure(full, CancellationToken.None);
            return after.Skipped > 0 ? (CleanOutcome.Unknown, 0) : (CleanOutcome.PartlyRemoved, Math.Max(0, before - after.Bytes));
        }
        catch (Exception)
        {
            return (CleanOutcome.Unknown, 0);
        }
    }

    // Whether a folder above the entry, below the checkout, is a link: git would not clean through it, and neither do we.
    private static bool IsBelowALink(string root, string path)
    {
        var slash = path.LastIndexOf('/');
        return slash > 0 && GitCheckoutReads.IsReachedThroughLink(root, path[..slash]);
    }

    private static string InRepository(string repository, string path) => repository.Length == 0 ? path : path[(repository.Length + 1)..];

    private static IEnumerable<List<IgnoredEntry>> Batches(string repository, List<IgnoredEntry> entries)
    {
        var batch = new List<IgnoredEntry>();
        var characters = 0;
        foreach (var entry in entries)
        {
            var length = InRepository(repository, entry.Path).Length + 16;
            if (batch.Count > 0 && (batch.Count >= MaximumBatchPaths || characters + length > MaximumBatchCharacters))
            {
                yield return batch;
                batch = [];
                characters = 0;
            }

            batch.Add(entry);
            characters += length;
        }

        if (batch.Count > 0) yield return batch;
    }
}
