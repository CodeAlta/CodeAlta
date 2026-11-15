using System.Diagnostics;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.Storage;

/// <summary>One entry git ignores in a checkout, as git names it: a folder an ignore rule matches once, else a file.</summary>
/// <param name="Path">The path relative to the checkout, with <c>/</c>; inside a submodule, prefixed with the path of the submodule.</param>
/// <param name="Folder">Whether the entry is a folder.</param>
/// <param name="Bytes">What it holds, links not followed.</param>
/// <param name="BuildOutput">Whether it is build output, which a build brings back: a folder whose own name is one of
/// <see cref="CodeAlta.Catalog.Storage.BuildOutput.Folders"/>, ignored by a rule of a <c>.gitignore</c> file of the working
/// tree of its repository, that holds no repository and was read whole; or a file named as one of <see cref="CodeAlta.Catalog.Storage.BuildOutput.SystemFiles"/>.</param>
public sealed record IgnoredEntry(string Path, bool Folder, long Bytes, bool BuildOutput)
{
    /// <summary>
    /// Gets the repository git named the entry in: empty for the checkout, else the path of the submodule relative to the
    /// checkout, with <c>/</c>, which starts <see cref="Path"/>.
    /// </summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Gets the number of files it holds.</summary>
    public long Files { get; init; }

    /// <summary>Gets the time of the most recent write of its files; null when it holds none.</summary>
    public DateTimeOffset? LastWrite { get; init; }

    /// <summary>
    /// Gets whether it is or holds a repository (<see cref="FolderUsage.HoldsRepository"/>), the git directory of the
    /// checkout included: such an entry is never build output.
    /// </summary>
    public bool HoldsRepository { get; init; }
}

/// <summary>A repository of a checkout, as the measure read it: the checkout, or one of its submodules.</summary>
/// <param name="Path">The path of the submodule relative to the checkout, with <c>/</c>; empty for the checkout.</param>
/// <param name="WorkTree">The physical path of its working tree, as git resolved it.</param>
/// <param name="GitDirectory">The absolute path of its git directory.</param>
/// <param name="CommonDirectory">The absolute path of the common git directory, the same as <paramref name="GitDirectory"/> except for a linked worktree.</param>
public sealed record CheckoutRepository(string Path, string WorkTree, string GitDirectory, string CommonDirectory)
{
    /// <summary>Gets the identity of the folder of its working tree when the measure read it, a link at its end not followed; null when it could not be read.</summary>
    public FileIdentity? WorkTreeIdentity { get; init; }

    /// <summary>Gets the identity of its git directory when the measure read it; null when it could not be read.</summary>
    public FileIdentity? GitDirectoryIdentity { get; init; }
}

/// <summary>How far a measure of a checkout is: not an estimated total.</summary>
/// <param name="ProcessedFiles">The files counted so far.</param>
public sealed record CheckoutUsageProgress(long ProcessedFiles);

/// <summary>
/// What a checkout (a project folder or a worktree) holds on disk, and how much of it git ignores. A measure of file
/// lengths, not of allocated space; links are never followed.
/// </summary>
/// <param name="RootPath">The full path of the checkout, as it was given.</param>
/// <param name="TotalBytes">Everything in the folder, the <c>.git</c> folder and the ignored files included, except the folders
/// measured on their own (<see cref="CheckoutUsage.ExcludedFolders"/>).</param>
/// <param name="TotalFiles">The files of the folder.</param>
/// <param name="GitBytes">The <c>.git</c> folder of the checkout and of its submodules, when it is a folder; 0 for a worktree, where <c>.git</c> is a file.</param>
/// <param name="BuildOutputBytes">The ignored entries that are build output.</param>
/// <param name="OtherIgnoredBytes">The other ignored entries, which a clone and a build do not bring back.</param>
/// <param name="OtherIgnoredCount">The number of those other ignored entries, empty ones included.</param>
/// <param name="IgnoredEntries">The ignored entries, largest first, at most <see cref="CheckoutUsageScanner.MaximumIgnoredEntries"/>.</param>
/// <param name="IgnoredEntriesTruncated">Whether there were more ignored entries than <paramref name="IgnoredEntries"/> lists; the sums count them all.</param>
/// <param name="SkippedEntries">Folders and files that could not be read, and parts left out of the sums because they would pass the range of a long: the figures are then a lower bound.</param>
/// <param name="GitKnown">Whether git could be read: false for a folder that is not a checkout, and then nothing is known as ignored.</param>
public sealed record CheckoutUsage(string RootPath, long TotalBytes, long TotalFiles, long GitBytes, long BuildOutputBytes, long OtherIgnoredBytes,
    long OtherIgnoredCount, IReadOnlyList<IgnoredEntry> IgnoredEntries, bool IgnoredEntriesTruncated, long SkippedEntries, bool GitKnown)
{
    /// <summary>Gets the repositories the measure read, the checkout first; empty when git was not read.</summary>
    public IReadOnlyList<CheckoutRepository> Repositories { get; init; } = [];

    /// <summary>
    /// Gets the folders inside the checkout that the measure left out because they are measured on their own (another
    /// checkout, the folder of another project, the profile): full paths as they were given. Nothing of them is in the
    /// figures or in <see cref="IgnoredEntries"/>, and an ignored entry that holds one is never build output. A clean that
    /// re-measures the checkout leaves out the same folders.
    /// </summary>
    public IReadOnlyList<string> ExcludedFolders { get; init; } = [];
}

/// <summary>
/// Measures one checkout: its folder, its <c>.git</c> folder, and the entries git ignores, read with
/// <c>git status --porcelain=v2 --ignored=matching -z</c> in the checkout and in its initialized submodules, and the rule
/// that ignores each candidate build folder, read with <c>git check-ignore -v</c>. Read-only: it deletes nothing, opens no
/// file and contacts no remote.
/// </summary>
public sealed class CheckoutUsageScanner
{
    /// <summary>The most ignored entries a measure lists.</summary>
    public const int MaximumIgnoredEntries = 500;

    // Submodules of submodules: a bound against a repository made to recurse, not a limit met in practice.
    private const int MaximumSubmoduleDepth = 16;

    // Paths of one `git check-ignore`: a command line stays far below the limits of every system.
    private const int CheckIgnoreBatch = 64;

    private readonly GitRunner _run;

    /// <summary>Creates a scanner over the git on the path, or over <paramref name="run"/>.</summary>
    /// <param name="run">Runs git; null runs <see cref="GitWorktreeService.RunGitAsync(string, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>.</param>
    public CheckoutUsageScanner(GitRunner? run = null) => _run = run ?? GitCheckoutReads.RunAsync;

    /// <summary>Measures a checkout.</summary>
    /// <param name="checkoutRoot">The folder of the checkout.</param>
    /// <param name="cancellationToken">Stops the measure.</param>
    /// <returns>What it holds.</returns>
    /// <exception cref="ArgumentException">The folder is null or empty.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<CheckoutUsage> ScanAsync(string checkoutRoot, CancellationToken cancellationToken)
        => ScanAsync(checkoutRoot, [], null, cancellationToken);

    /// <summary>Measures a checkout, reporting how many files it counted.</summary>
    /// <param name="checkoutRoot">The folder of the checkout.</param>
    /// <param name="progress">Told the files counted so far, at most four times a second and once at the end, on a worker thread.</param>
    /// <param name="cancellationToken">Stops the measure.</param>
    /// <returns>What it holds.</returns>
    /// <exception cref="ArgumentException">The folder is null or empty.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public Task<CheckoutUsage> ScanAsync(string checkoutRoot, IProgress<CheckoutUsageProgress>? progress, CancellationToken cancellationToken)
        => ScanAsync(checkoutRoot, [], progress, cancellationToken);

    /// <summary>
    /// Measures a checkout without the folders inside it that are measured on their own, so that no byte is counted twice:
    /// they are left out of the figures and of the ignored entries, and an ignored entry that holds one is never build
    /// output. The ignore file of a folder that holds one of them (the one CodeAlta writes beside the worktrees it places
    /// inside a project) is counted in the total, not as an ignored file of the checkout.
    /// </summary>
    /// <param name="checkoutRoot">The folder of the checkout.</param>
    /// <param name="excludedFolders">Full paths of folders to leave out; those that are not inside the checkout are ignored.</param>
    /// <param name="progress">Told the files counted so far, at most four times a second and once at the end, on a worker thread.</param>
    /// <param name="cancellationToken">Stops the measure.</param>
    /// <returns>What it holds.</returns>
    /// <exception cref="ArgumentException">The folder is null or empty, or an excluded folder is not a path.</exception>
    /// <exception cref="ArgumentNullException">The excluded folders are null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public async Task<CheckoutUsage> ScanAsync(string checkoutRoot, IReadOnlyCollection<string> excludedFolders, IProgress<CheckoutUsageProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        ArgumentNullException.ThrowIfNull(excludedFolders);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutRoot));
        string[] excludedGiven = [.. excludedFolders.Select(static folder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))];
        progress?.Report(new(0));
        var read = new Reading();
        var known = Directory.Exists(root) && await ReadIgnoredAsync(root, string.Empty, 0, read, cancellationToken).ConfigureAwait(false);
        string walkRoot;
        if (known)
        {
            // The physical path git resolved: the git directories it names compare with what the walk finds.
            walkRoot = read.Repositories[0].WorkTree;
        }
        else
        {
            read = new Reading();
            // The checkout itself may be reached through a link the user chose; nothing inside it is followed.
            walkRoot = Directory.Exists(root) && Directory.ResolveLinkTarget(root, returnFinalTarget: true) is { } target ? target.FullName : root;
        }

        // The excluded folders as the walk names them: from its root, which may be the physical path of the one given.
        var comparer = known ? GitCheckoutReads.PathComparerOf(walkRoot) : CheckoutPaths.ComparerOf(walkRoot);
        var excludedInWalk = new List<string>();
        foreach (var folder in excludedGiven)
        {
            var physical = CheckoutPaths.Physical(folder);
            var from = CheckoutPaths.IsWithin(folder, root, comparer) ? Path.GetRelativePath(root, folder)
                : CheckoutPaths.IsWithin(physical, walkRoot, comparer) ? Path.GetRelativePath(walkRoot, physical) : null;
            if (from is not null && from != ".") excludedInWalk.Add(Path.Combine(walkRoot, from));
        }

        var usage = await Task.Run(() => Measure(walkRoot, known, read, excludedInWalk, comparer, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        return usage with { RootPath = root, Repositories = known ? read.Repositories : [], ExcludedFolders = excludedGiven };
    }

    // What the git reads of a measure gather, for the walk.
    private sealed class Reading
    {
        public List<(string Path, bool Folder, string Repository)> Ignored { get; } = [];
        public List<CheckoutRepository> Repositories { get; } = [];
        // Without case whatever the volume: a match keeps a folder.
        public HashSet<string> GitDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RepositoryRuled { get; } = new(StringComparer.Ordinal);
    }

    // The ignored entries of one checkout and of its submodules; false when one of them cannot be read.
    private async Task<bool> ReadIgnoredAsync(string folder, string prefix, int depth, Reading read, CancellationToken cancellationToken)
    {
        if (depth > MaximumSubmoduleDepth || !await GitCheckoutReads.IsCheckoutRootAsync(_run, folder, cancellationToken).ConfigureAwait(false)) return false;
        if (await GitCheckoutReads.ReadAsync(_run, folder, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false) is not { } topText
            || GitCheckoutReads.Line(topText) is not { Length: > 0 } topLine)
            return false;
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLine));
        if (await GitCheckoutReads.GitDirectoryAsync(_run, folder, cancellationToken).ConfigureAwait(false) is not { } gitDirectory) return false;
        if (await GitCheckoutReads.ReadAsync(_run, folder, ["rev-parse", "--git-common-dir"], cancellationToken).ConfigureAwait(false) is not { } commonText
            || GitCheckoutReads.Line(commonText) is not { Length: > 0 } commonLine)
            return false;
        var common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(top, commonLine)));
        // What the folders are, not only their paths: a repository created at the same paths is another one.
        read.Repositories.Add(new(prefix, top, gitDirectory, common) { WorkTreeIdentity = FileIdentity.Of(top), GitDirectoryIdentity = FileIdentity.Of(gitDirectory) });
        read.GitDirectories.Add(gitDirectory);
        read.GitDirectories.Add(common);

        // "matching" names the folder an ignore pattern matches (src/old/obj/), not a parent that holds nothing else (src/old/):
        // a folder left with only its build folders is build output, not other ignored files.
        if (await GitCheckoutReads.ReadAsync(_run, folder, ["status", "--porcelain=v2", "--ignored=matching", "--untracked-files=normal", "-z"], cancellationToken)
                .ConfigureAwait(false) is not { } status)
            return false;
        var candidates = new List<string>();
        var records = status.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            // A rename or copy record is followed by the path it came from, as a record of its own.
            if (record.StartsWith("2 ", StringComparison.Ordinal)) { index++; continue; }
            if (!record.StartsWith("! ", StringComparison.Ordinal)) continue;
            var path = record[2..];
            var folderEntry = path.EndsWith('/');
            path = path.TrimEnd('/');
            if (path.Length == 0) continue;
            read.Ignored.Add((GitCheckoutReads.Prefixed(prefix, path), folderEntry, prefix));
            if (folderEntry && BuildOutput.Includes(path, folder: true)) candidates.Add(path);
        }

        if (!await ReadRulesAsync(folder, prefix, top, candidates, read.RepositoryRuled, cancellationToken).ConfigureAwait(false)) return false;

        if (await GitCheckoutReads.InitializedSubmodulesAsync(_run, folder, cancellationToken).ConfigureAwait(false) is not { } submodules) return false;
        foreach (var submodule in submodules)
        {
            var inside = Path.Combine(folder, submodule.Replace('/', Path.DirectorySeparatorChar));
            if (!await ReadIgnoredAsync(inside, GitCheckoutReads.Prefixed(prefix, submodule), depth + 1, read, cancellationToken).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    // Which candidate build folders a rule of a .gitignore of the working tree ignores: a rule of .git/info/exclude or of
    // core.excludesFile is the user's own, which hides personal content more often than build output.
    private async Task<bool> ReadRulesAsync(string folder, string prefix, string top, List<string> candidates, HashSet<string> ruled,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return true;
        // The file core.excludesFile names, where it physically is: it is the user's even when it is a .gitignore of the tree.
        var excludes = await _run(folder, ["config", "--path", "--get", "core.excludesFile"], GitWorktreeService.ReadTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string? excludesFile = null;
        if (excludes.Succeeded && GitCheckoutReads.Line(excludes.Output) is { Length: > 0 } value)
        {
            if (Physical(top, value) is not { } physical) return false;
            excludesFile = physical;
        }
        else if (!excludes.Succeeded && excludes.ExitCode != 1)
        {
            // Exit code 1: not set. Anything else: not known.
            return false;
        }

        foreach (var batch in candidates.Chunk(CheckIgnoreBatch))
        {
            // One line per path, in order, matching or not. `./` keeps a name that starts with `:` from being read as the
            // magic of a pathspec, which check-ignore parses and whose `literal` it refuses.
            var arguments = new List<string>(batch.Length + 4) { "check-ignore", "-v", "--non-matching", "--" };
            arguments.AddRange(batch.Select(static path => "./" + path));
            if (await GitCheckoutReads.ReadAsync(_run, folder, arguments, cancellationToken).ConfigureAwait(false) is not { } answer) return false;
            var lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != batch.Length) return false;
            for (var index = 0; index < batch.Length; index++)
            {
                if (IsRuleOfTheRepository(lines[index], batch[index], top, excludesFile)) ruled.Add(GitCheckoutReads.Prefixed(prefix, batch[index]));
            }
        }

        return true;
    }

    // Whether a line `<source>:<line>:<pattern><TAB><path>` of check-ignore names, as its source, exactly one of the
    // .gitignore files of the folders above the entry, relative to the top (`.gitignore`, `a/.gitignore` for `a/bin`),
    // followed by `:<digits>:`; that file is physically in the working tree, and is not the file of core.excludesFile.
    // Nothing is cut at a colon: a git directory can be named `policy/.gitignore:1:x`. A source git quotes (a name with a
    // quote, a backslash or a control character) matches none: on the side of what is not build output.
    private static bool IsRuleOfTheRepository(string line, string path, string top, string? excludesFile)
    {
        foreach (var source in IgnoreFilesAbove(path))
        {
            if (!line.StartsWith(source + ":", StringComparison.Ordinal)) continue;
            var rest = line.AsSpan(source.Length + 1);
            var digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
            if (digits == 0 || digits >= rest.Length || rest[digits] != ':') continue;
            if (GitCheckoutReads.IsReachedThroughLink(top, source)) return false;
            var full = Path.Combine(top, source.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return false;
            // Without case: a match keeps the folder.
            return excludesFile is null || !string.Equals(full, excludesFile, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IEnumerable<string> IgnoreFilesAbove(string path)
    {
        yield return ".gitignore";
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1)) yield return path[..slash] + "/.gitignore";
    }

    // A path made physical: each link on the way is replaced by what it points to. Null when it cannot be resolved.
    private static string? Physical(string top, string path)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(top, path));
            var root = Path.GetPathRoot(full)!;
            var current = root;
            foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (new FileInfo(current).LinkTarget is not null && new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }

            return current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Measures one part; the tests replace it to reach what real folders cannot, such as sums past the range of a long.
    internal Func<string, IReadOnlySet<string>?, IReadOnlySet<string>?, Action?, CancellationToken, FolderUsage> MeasurePart { get; init; } = FolderUsage.Measure;

    private CheckoutUsage Measure(string root, bool known, Reading read, IReadOnlyList<string> excludedFolders, StringComparer comparer,
        IProgress<CheckoutUsageProgress>? progress, CancellationToken cancellationToken)
    {
        // The .git folder is a fact of the disk, also when git cannot read the checkout.
        IEnumerable<string> gitFolders = known ? read.Repositories.Select(static repository => repository.Path) : [string.Empty];
        long counted = 0, lastReport = 0;
        void Counted()
        {
            counted++;
            if (progress is null || (lastReport != 0 && Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds < 250)) return;
            progress.Report(new(counted));
            lastReport = Stopwatch.GetTimestamp();
        }

        string Full(string relative) => relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        long totalBytes = 0, totalFiles = 0, gitBytes = 0, buildBytes = 0, otherBytes = 0, otherCount = 0, skipped = 0;
        // A part whose bytes would carry a sum past the range of a long is left out and counted as skipped, never wrapped.
        bool Add(ref long category, FolderUsage usage)
        {
            skipped = checked(skipped + usage.Skipped);
            long bytes = totalBytes, files = totalFiles, part = category;
            if (!GitCheckoutReads.TryAdd(ref bytes, usage.Bytes) || !GitCheckoutReads.TryAdd(ref files, usage.Files) || !GitCheckoutReads.TryAdd(ref part, usage.Bytes))
            {
                skipped++;
                return false;
            }

            (totalBytes, totalFiles, category) = (bytes, files, part);
            return true;
        }

        // Each part is walked once: the walk of the folder leaves out what is measured on its own. Paths compare as the
        // file system does, and a path named twice is measured once.
        // As the volume of the checkout compares names: on one that tells case apart, `bin` and `Bin` are two folders.
        var excluded = new HashSet<string>(comparer);
        // The folders measured on their own, and the ignore files of the folders that hold them.
        excluded.UnionWith(excludedFolders);
        var markers = new HashSet<string>(excludedFolders.Select(static folder => Path.Combine(Path.GetDirectoryName(folder)!, ".gitignore")), comparer);
        foreach (var prefix in gitFolders)
        {
            var git = Path.Combine(Full(prefix), ".git");
            if (!Directory.Exists(git) || !excluded.Add(git)) continue;
            Add(ref gitBytes, MeasurePart(git, null, null, Counted, cancellationToken));
        }

        var entries = new List<IgnoredEntry>(read.Ignored.Count);
        foreach (var (path, folder, repository) in read.Ignored)
        {
            var full = Full(path);
            if ((!folder && markers.Contains(full)) || excludedFolders.Any(other => CheckoutPaths.IsWithin(full, other, comparer))) continue;
            if (!excluded.Add(full)) continue;
            // A folder measured on its own inside it is left out of it, and makes it more than build output.
            var holding = folder ? excludedFolders.Where(other => CheckoutPaths.IsWithin(other, full, comparer)).ToHashSet(comparer) : null;
            // One walk measures it and finds a repository in it.
            var usage = MeasurePart(full, holding is { Count: > 0 } ? holding : null, read.GitDirectories, Counted, cancellationToken);
            // A folder that holds only folders measured on their own is no ignored file of the checkout.
            if (holding is { Count: > 0 } && usage is { Files: 0, Skipped: 0 }) continue;
            // What was not read whole may hold a repository: not having seen one is no proof.
            var build = !usage.HoldsRepository && usage.Skipped == 0 && holding is not { Count: > 0 }
                && (folder ? read.RepositoryRuled.Contains(path) : BuildOutput.Includes(path, folder: false));
            if (build) Add(ref buildBytes, usage);
            else
            {
                Add(ref otherBytes, usage);
                otherCount++;
            }

            entries.Add(new(path, folder, usage.Bytes, build)
            {
                Repository = repository, Files = usage.Files, LastWrite = usage.LastWrite, HoldsRepository = usage.HoldsRepository,
            });
        }

        long rest = 0;
        Add(ref rest, MeasurePart(root, excluded, null, Counted, cancellationToken));
        progress?.Report(new(counted));
        entries.Sort(static (left, right) => right.Bytes != left.Bytes ? right.Bytes.CompareTo(left.Bytes) : string.CompareOrdinal(left.Path, right.Path));
        var truncated = entries.Count > MaximumIgnoredEntries;
        return new(root, totalBytes, totalFiles, gitBytes, buildBytes, otherBytes, otherCount,
            truncated ? entries.GetRange(0, MaximumIgnoredEntries) : entries, truncated, skipped, known);
    }
}
