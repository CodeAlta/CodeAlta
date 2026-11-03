using System.Text.Json;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// The folder of versions of a machine (<c>fleet/versions</c> of a node's state root, <c>versions</c> of a server's data
/// root): each version side by side in a folder of its own, never replaced while it runs, and <c>state.json</c>, the
/// pointer and the trial (<see cref="UpdateState"/>). Several processes change the state (the launcher, the running
/// version, <c>update</c>): each change is made under <c>state.lock</c> and written atomically.
/// </summary>
public sealed class UpdateVersionStore
{
    /// <summary>The name of the folder where a package is unpacked before it takes its place.</summary>
    public const string StagingFolderName = ".staging";

    private const string StateFileName = "state.json";
    private const string LockFileName = "state.lock";
    private static readonly TimeSpan LockPatience = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="UpdateVersionStore"/> class.</summary>
    /// <param name="root">The folder of versions, an absolute path; created when needed.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is not absolute.</exception>
    public UpdateVersionStore(string root, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException($"The folder of versions must be an absolute path: '{root}'.", nameof(root));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the folder of versions.</summary>
    public string Root { get; }

    /// <summary>Gets the clock of the store.</summary>
    public TimeProvider Time => _time;

    /// <summary>Gets the folder of a version.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The folder: <c>&lt;root&gt;/&lt;version&gt;</c>.</returns>
    public string VersionPath(UpdateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Path.Combine(Root, version.ToString());
    }

    /// <summary>Gets whether a path is inside the folder of versions (a process that runs a selected version).</summary>
    /// <param name="path">A folder or a file.</param>
    /// <returns>Whether it is under <see cref="Root"/>.</returns>
    public bool Contains(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(Root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Gets whether a version is staged: its folder holds the entry assembly of the product.</summary>
    /// <param name="version">The version.</param>
    /// <param name="product">The product.</param>
    /// <returns>Whether it can be started.</returns>
    public bool IsStaged(UpdateVersion version, UpdateProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return File.Exists(Path.Combine(VersionPath(version), product.AssemblyFileName));
    }

    /// <summary>Reads the state. A state that is missing or cannot be read is the empty state: the installed entry point runs.</summary>
    /// <returns>The state.</returns>
    public UpdateState Read()
    {
        var path = Path.Combine(Root, StateFileName);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, UpdateJsonContext.Default.UpdateState) ?? new UpdateState();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new UpdateState();
        }
        catch (IOException)
        {
            return new UpdateState();
        }
    }

    /// <summary>Changes the state under its lock: reads it, applies the change, writes the result atomically.</summary>
    /// <param name="change">The change; returning the same instance writes nothing.</param>
    /// <returns>The state after the change.</returns>
    /// <exception cref="UpdateException">The lock could not be taken, or the state could not be written.</exception>
    public UpdateState Change(Func<UpdateState, UpdateState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var gate = AcquireLock();
        var before = Read();
        var after = change(before);
        if (ReferenceEquals(before, after)) return before;
        var path = Path.Combine(Root, StateFileName);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(after, UpdateJsonContext.Default.UpdateState));
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            throw new UpdateException(UpdateErrorCodes.StateUnavailable, $"{path} could not be written: {exception.Message}", exception);
        }

        return after;
    }

    /// <summary>Asks the running process to switch to a staged version (it takes it within seconds), or the next start when none runs.</summary>
    /// <param name="version">The staged version.</param>
    /// <param name="now">Whether the switch does not wait for running work.</param>
    /// <param name="requestedBy">Who asks: <c>local</c>, or what the fleet names.</param>
    /// <returns>The state.</returns>
    public UpdateState RequestSwitch(UpdateVersion version, bool now, string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(version);
        var at = _time.GetUtcNow();
        return Change(state => state with { Pending = new UpdatePending(version.ToString(), at, now, UpdatePhases.Requested, at, 0, requestedBy) });
    }

    /// <summary>Records where a pending switch stands, if it is still the one of that version.</summary>
    /// <param name="version">The version of the pending switch.</param>
    /// <param name="phase">Its phase (<see cref="UpdatePhases"/>).</param>
    /// <param name="sessionsAtWork">The sessions a drain waits for.</param>
    /// <returns>The state.</returns>
    public UpdateState SetPhase(UpdateVersion version, string phase, int sessionsAtWork = 0)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Change(state =>
        {
            if (state.Pending is not { } pending || !Same(pending.Version, version)) return state;
            if (pending.Phase == phase && pending.SessionsAtWork == sessionsAtWork) return state;
            return state with { Pending = pending with { Phase = phase, PhaseSince = pending.Phase == phase ? pending.PhaseSince : _time.GetUtcNow(), SessionsAtWork = sessionsAtWork } };
        });
    }

    /// <summary>Drops the pending switch, and records why when it failed.</summary>
    /// <param name="message">Why it did not happen; null to drop it without an outcome.</param>
    /// <returns>The state.</returns>
    public UpdateState CancelPending(string? message)
        => Change(state => state.Pending is not { } pending ? state : state with
        {
            Pending = null,
            LastOutcome = message is null ? state.LastOutcome : new UpdateOutcome(pending.Version, UpdateOutcomes.Failed, _time.GetUtcNow(), message),
        });

    /// <summary>
    /// Selects a staged version on trial: the pointer moves to it, the version it replaces becomes the one a rollback goes
    /// back to, and the pending switch is done. The next start of the entry point runs it.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <param name="backup">The backup taken before the switch, restored by a rollback; null for none.</param>
    /// <returns>The state.</returns>
    public UpdateState Select(UpdateVersion version, string? backup = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var at = _time.GetUtcNow();
        return Change(state => state with
        {
            Previous = Same(state.Current, version) ? state.Previous : state.Current,
            Current = version.ToString(),
            Trial = new UpdateTrial(version.ToString(), at, 0, backup),
            Pending = null,
        });
    }

    /// <summary>
    /// Takes the switch that waits, when no process of the product runs (at start, or in a launcher between two
    /// versions): a staged version is selected on trial at once, since nothing has to drain; one that is not staged, or
    /// already selected, is dropped.
    /// </summary>
    /// <param name="product">The product.</param>
    /// <returns>The version selected, or null when none was.</returns>
    public UpdateVersion? TakePending(UpdateProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (Read().Pending is not { } pending) return null;
        if (!UpdateVersion.TryParse(pending.Version, out var version) || !IsStaged(version, product))
        {
            CancelPending($"{pending.Version} is not staged in {Root}.");
            return null;
        }

        if (Same(Read().Current, version))
        {
            CancelPending(null);
            return null;
        }

        Select(version);
        return version;
    }

    /// <summary>Counts a start of the version on trial, before a launcher starts it.</summary>
    /// <returns>The state.</returns>
    public UpdateState CountTrialStart()
        => Change(state => state.Trial is not { } trial ? state : state with { Trial = trial with { Starts = trial.Starts + 1 } });

    /// <summary>Confirms the version on trial, called by that version once it is healthy.</summary>
    /// <param name="version">The version that runs.</param>
    /// <returns>Whether it was on trial and is now confirmed.</returns>
    public bool Confirm(UpdateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var confirmed = false;
        Change(state =>
        {
            if (state.Trial is not { } trial || !Same(trial.Version, version) || !Same(state.Current, version)) return state;
            confirmed = true;
            return state with { Trial = null, LastOutcome = new UpdateOutcome(trial.Version, UpdateOutcomes.Confirmed, _time.GetUtcNow()) };
        });
        return confirmed;
    }

    /// <summary>Rolls the version on trial back: the pointer returns to the previous version and the trial is marked bad.</summary>
    /// <param name="reason">Why, for a person.</param>
    /// <returns>The trial rolled back (its backup is restored by the caller), or null when no version was on trial.</returns>
    public UpdateTrial? RollBack(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        UpdateTrial? rolledBack = null;
        Change(state =>
        {
            if (state.Trial is not { } trial) return state;
            rolledBack = trial;
            var at = _time.GetUtcNow();
            return state with
            {
                Current = state.Previous,
                Previous = null,
                Trial = null,
                Bad = [.. state.Bad.Where(bad => !string.Equals(bad.Version, trial.Version, StringComparison.OrdinalIgnoreCase)), new UpdateBadVersion(trial.Version, at, reason)],
                LastOutcome = new UpdateOutcome(trial.Version, UpdateOutcomes.RolledBack, at, reason),
            };
        });
        return rolledBack;
    }

    /// <summary>
    /// Removes the folders of the versions nothing needs: all but the selected one, the one a rollback goes back to, the
    /// one on trial and the one a switch waits for; and staging folders older than a day. A folder in use (a process
    /// still runs it on Windows) stays until the next time.
    /// </summary>
    /// <returns>The versions removed.</returns>
    public IReadOnlyList<string> Prune()
    {
        if (!Directory.Exists(Root)) return [];
        var state = Read();
        var keep = new[] { state.Current, state.Previous, state.Trial?.Version, state.Pending?.Version }.OfType<string>().ToArray();
        var removed = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(folder);
            if (!UpdateVersion.TryParse(name, out var version) || keep.Any(kept => Same(kept, version))) continue;
            if (TryRemoveVersion(folder)) removed.Add(name);
        }

        var staging = Path.Combine(Root, StagingFolderName);
        if (Directory.Exists(staging))
        {
            foreach (var folder in Directory.EnumerateDirectories(staging))
            {
                if (_time.GetUtcNow() - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromDays(1)) TryDeleteDirectory(folder);
            }
        }

        return removed;
    }

    /// <summary>Gets whether a version as the state writes it is a given version.</summary>
    /// <param name="text">The version as written; null for none.</param>
    /// <param name="version">The version.</param>
    /// <returns>Whether the text is that version.</returns>
    public static bool Same(string? text, UpdateVersion version) => UpdateVersion.TryParse(text, out var parsed) && parsed.Equals(version);

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, LockFileName);
        var deadline = DateTime.UtcNow + LockPatience;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(UpdateErrorCodes.StateUnavailable, $"{path} stays locked: {exception.Message}", exception);
            }
        }
    }

    /// <summary>
    /// Removes the folder of a version so that it is never left half deleted: it is moved out of the folder of versions
    /// first (which Windows refuses while a process runs from it), then deleted; what cannot be deleted yet stays in the
    /// staging folder, cleaned later.
    /// </summary>
    /// <param name="folder">The folder of the version.</param>
    /// <returns>Whether the version is gone.</returns>
    public bool TryRemoveVersion(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder)) return true;
        var trash = Path.Combine(Root, StagingFolderName, "removed-" + Path.GetFileName(folder) + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            Directory.Move(folder, trash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        TryDeleteDirectory(trash);
        return true;
    }

    private static bool TryDeleteDirectory(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
