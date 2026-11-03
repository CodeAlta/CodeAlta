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
    private const string LauncherLockFileName = "launcher.lock";
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

    /// <summary>
    /// Reads the state as <see cref="Read"/> does, but tells a state that cannot be read from a missing one: a missing
    /// state is the empty state, while one that exists and does not read (corrupt, locked, not allowed) throws. What decides
    /// that a version confirmed reads it so: an unreadable state is never taken for a state without a trial.
    /// </summary>
    /// <returns>The state.</returns>
    /// <exception cref="UpdateException">The state exists and cannot be read (<see cref="UpdateErrorCodes.StateUnavailable"/>).</exception>
    public UpdateState ReadOrThrow()
    {
        var path = Path.Combine(Root, StateFileName);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, UpdateJsonContext.Default.UpdateState)
                ?? throw new UpdateException(UpdateErrorCodes.StateUnavailable, $"{path} holds no state.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UpdateState();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateErrorCodes.StateUnavailable, $"{path} cannot be read: {exception.Message}", exception);
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

    /// <summary>
    /// Asks for a switch only when a condition holds under the lock of the state, atomically: the fleet's switch does not
    /// replace one the owner asked for, and is not published once its campaign was cancelled.
    /// </summary>
    /// <param name="version">The staged version.</param>
    /// <param name="now">Whether the switch does not wait for running work.</param>
    /// <param name="requestedBy">Who asks.</param>
    /// <param name="mayReplace">Whether the switch may be asked for, given the one that waits (null for none).</param>
    /// <returns>Whether it was asked for.</returns>
    /// <exception cref="UpdateException">The lock could not be taken, or the state could not be written.</exception>
    public bool TryRequestSwitch(UpdateVersion version, bool now, string requestedBy, Func<UpdatePending?, bool> mayReplace)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(requestedBy);
        ArgumentNullException.ThrowIfNull(mayReplace);
        var at = _time.GetUtcNow();
        var requested = false;
        Change(state =>
        {
            if (!mayReplace(state.Pending)) return state;
            requested = true;
            return state with { Pending = new UpdatePending(version.ToString(), at, now, UpdatePhases.Requested, at, 0, requestedBy) };
        });
        return requested;
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

    /// <summary>Drops the pending switch only when it is the one a requester asked for (a cancelled campaign), atomically.</summary>
    /// <param name="message">Why it did not happen; null to drop it without an outcome.</param>
    /// <param name="requestedBy">Who asked for the switch to drop (<see cref="UpdatePending.RequestedBy"/>).</param>
    /// <returns>Whether that switch waited and was dropped.</returns>
    /// <exception cref="UpdateException">The lock could not be taken, or the state could not be written.</exception>
    public bool CancelPending(string? message, string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        var dropped = false;
        Change(state =>
        {
            if (state.Pending is not { } pending || !string.Equals(pending.RequestedBy, requestedBy, StringComparison.Ordinal)) return state;
            dropped = true;
            return state with
            {
                Pending = null,
                LastOutcome = message is null ? state.LastOutcome : new UpdateOutcome(pending.Version, UpdateOutcomes.Failed, _time.GetUtcNow(), message),
            };
        });
        return dropped;
    }

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
        return Change(state => Selected(state, version, at, backup, state.Pending is { } pending && Same(pending.Version, version) ? pending.RequestedBy : null));
    }

    /// <summary>
    /// Selects a staged version on trial, as <see cref="Select"/>, only while the switch that waits is still the one the
    /// caller read (<paramref name="expected"/>: the same version, asked at the same time by the same requester),
    /// atomically under the lock of the state: a switch withdrawn meanwhile (a cancelled campaign) is not taken, and a
    /// newer one (another <c>update</c>) is not cleared.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <param name="expected">The switch that waited when the caller read the state.</param>
    /// <param name="backup">The backup taken before the switch, restored by a rollback; null for none.</param>
    /// <returns>Whether that switch still waited and its version is now selected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> or <paramref name="expected"/> is null.</exception>
    /// <exception cref="UpdateException">The lock could not be taken, or the state could not be written.</exception>
    public bool TrySelect(UpdateVersion version, UpdatePending expected, string? backup = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(expected);
        var at = _time.GetUtcNow();
        var selected = false;
        Change(state =>
        {
            if (!SameRequest(state.Pending, expected)) return state;
            selected = true;
            return Selected(state, version, at, backup, expected.RequestedBy);
        });
        return selected;
    }

    /// <summary>Drops the pending switch only while it is still the one the caller read, atomically; a newer one stays.</summary>
    /// <param name="expected">The switch that waited when the caller read the state.</param>
    /// <param name="message">Why it did not happen; null to drop it without an outcome.</param>
    /// <returns>Whether that switch still waited and was dropped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expected"/> is null.</exception>
    /// <exception cref="UpdateException">The lock could not be taken, or the state could not be written.</exception>
    public bool TryCancelPending(UpdatePending expected, string? message)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var dropped = false;
        Change(state =>
        {
            if (state.Pending is not { } pending || !SameRequest(pending, expected)) return state;
            dropped = true;
            return state with
            {
                Pending = null,
                LastOutcome = message is null ? state.LastOutcome : new UpdateOutcome(pending.Version, UpdateOutcomes.Failed, _time.GetUtcNow(), message),
            };
        });
        return dropped;
    }

    private static UpdateState Selected(UpdateState state, UpdateVersion version, DateTimeOffset at, string? backup, string? selectedBy)
        => state with
        {
            Previous = Same(state.Current, version) ? state.Previous : state.Current,
            Current = version.ToString(),
            SelectedBy = selectedBy,
            Trial = new UpdateTrial(version.ToString(), at, 0, backup),
            Pending = null,
        };

    // Whether a pending switch is the request a caller read: its phase may have moved on, not who asked, when and for what.
    private static bool SameRequest(UpdatePending? pending, UpdatePending expected)
        => pending is not null && string.Equals(pending.Version, expected.Version, StringComparison.OrdinalIgnoreCase) && pending.RequestedAt == expected.RequestedAt
           && pending.Now == expected.Now && string.Equals(pending.RequestedBy, expected.RequestedBy, StringComparison.Ordinal);

    /// <summary>
    /// Gives way to the installed entry point when the owner changed it since a version was selected: a
    /// <c>dotnet tool update</c> to a version newer than the selected one drops the pointer, so that the installed entry
    /// point runs, and the folders of versions nothing needs any more are pruned. A version selected older than the
    /// installed one (<c>update --version</c>) stays as long as the installed entry point does not change, and nothing
    /// changes while a rollback waits for its restore. Otherwise the installed version is recorded while a version is
    /// selected, to tell a later change.
    /// </summary>
    /// <param name="installedVersion">The version of the installed entry point; null when unknown.</param>
    /// <returns>The selected version dropped, or null when the pointer stays.</returns>
    /// <exception cref="UpdateException">The lock could not be taken, or the state could not be written.</exception>
    public string? ReconcileInstalled(string? installedVersion)
    {
        if (!UpdateVersion.TryParse(installedVersion, out var installed)) return null;
        var state = Read();
        if (state.Current is null || state.RollingBack is not null || Same(state.Installed, installed)) return null;
        string? dropped = null;
        Change(current =>
        {
            if (current.Current is null || current.RollingBack is not null || Same(current.Installed, installed)) return current;
            if (current.Installed is not null && UpdateVersion.TryParse(current.Current, out var selected) && installed > selected)
            {
                dropped = current.Current;
                return current with { Current = null, SelectedBy = null, Previous = null, Trial = null, Installed = installed.ToString() };
            }

            return current with { Installed = installed.ToString() };
        });
        if (dropped is not null) Prune();
        return dropped;
    }

    /// <summary>
    /// Takes the switch that waits, when no process of the product runs (at start, or in a launcher between two
    /// versions): a staged version is selected on trial at once, since nothing has to drain; one that is not staged, or
    /// already selected, is dropped. Nothing is taken while a rollback waits for its restore.
    /// </summary>
    /// <param name="product">The product.</param>
    /// <returns>The version selected, or null when none was.</returns>
    public UpdateVersion? TakePending(UpdateProduct product) => TakePending(product, null);

    /// <summary>
    /// Takes the switch that waits, as <see cref="TakePending(UpdateProduct)"/>, after a copy of what the new version may
    /// change (the server's database): the trial records the copy, which a rollback restores. A copy that fails drops the
    /// switch, since without it there is no way back.
    /// </summary>
    /// <param name="product">The product.</param>
    /// <param name="backup">
    /// Copies the data before the switch to a version and gives the path of the copy, or null when there is nothing to
    /// copy; it throws <see cref="UpdateException"/>, <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// when it fails. Null when the product needs no copy.
    /// </param>
    /// <returns>The version selected, or null when none was.</returns>
    public UpdateVersion? TakePending(UpdateProduct product, Func<UpdateVersion, string?>? backup)
    {
        ArgumentNullException.ThrowIfNull(product);
        var state = Read();
        if (state.Pending is not { } pending || state.RollingBack is not null) return null;
        if (!UpdateVersion.TryParse(pending.Version, out var version) || !IsStaged(version, product))
        {
            TryCancelPending(pending, $"{pending.Version} is not staged in {Root}.");
            return null;
        }

        if (Same(Read().Current, version))
        {
            TryCancelPending(pending, null);
            return null;
        }

        string? copy = null;
        if (backup is not null)
        {
            SetPhase(version, UpdatePhases.BackingUp);
            try
            {
                copy = backup(version);
            }
            catch (Exception exception) when (exception is UpdateException or IOException or UnauthorizedAccessException)
            {
                TryCancelPending(pending, $"The data could not be copied before the switch: {exception.Message}");
                return null;
            }
        }

        // An update asked meanwhile (during the copy) replaced or withdrew this switch: it waits for the next start.
        return TrySelect(version, pending, copy) ? version : null;
    }

    /// <summary>
    /// Takes the lease of the launcher of this folder of versions (<c>launcher.lock</c>), unless another process holds
    /// it: the one process that selects a version at start, runs a version as its child, follows its trial and rolls it
    /// back. The operating system releases it when that process ends.
    /// </summary>
    /// <returns>The lease, to dispose once the launcher ends; null when another process holds it.</returns>
    /// <exception cref="UpdateException">The folder of versions cannot be created, or its lease cannot be opened.</exception>
    public UpdateLauncherLease? TryAcquireLauncherLease()
    {
        var path = Path.Combine(Root, LauncherLockFileName);
        FileStream stream;
        try
        {
            Directory.CreateDirectory(Root);
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateErrorCodes.StateUnavailable, $"{path} could not be opened: {exception.Message}", exception);
        }

        try
        {
            // The process that holds it, for a person who looks.
            stream.SetLength(0);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            stream.Flush();
        }
        catch (IOException)
        {
        }

        return new UpdateLauncherLease(stream);
    }

    /// <summary>Counts a start of the version on trial, before a launcher starts it.</summary>
    /// <returns>The state.</returns>
    public UpdateState CountTrialStart()
        => Change(state => state.Trial is not { } trial ? state : state with { Trial = trial with { Starts = trial.Starts + 1 } });

    /// <summary>Takes back the start of a version on trial that did not run: another process held what it runs on.</summary>
    /// <param name="version">The version on trial.</param>
    /// <returns>The state.</returns>
    public UpdateState UncountTrialStart(UpdateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Change(state => state.Trial is not { Starts: > 0 } trial || !Same(trial.Version, version) ? state : state with { Trial = trial with { Starts = trial.Starts - 1 } });
    }

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

    /// <summary>
    /// Rolls the version on trial back: the pointer returns to the previous version and the trial is marked bad. A trial
    /// with a backup is recorded as <see cref="UpdateState.RollingBack"/> in the same write, until
    /// <see cref="CompleteRollback"/> says that the backup is restored.
    /// </summary>
    /// <param name="reason">Why, for a person.</param>
    /// <returns>The trial rolled back, or null when no version was on trial.</returns>
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
                SelectedBy = null,
                Previous = null,
                Trial = null,
                RollingBack = trial.Backup is null ? state.RollingBack : new UpdateRollback(trial, at, reason),
                Bad = [.. state.Bad.Where(bad => !string.Equals(bad.Version, trial.Version, StringComparison.OrdinalIgnoreCase)), new UpdateBadVersion(trial.Version, at, reason)],
                LastOutcome = new UpdateOutcome(trial.Version, UpdateOutcomes.RolledBack, at, reason),
            };
        });
        return rolledBack;
    }

    /// <summary>Records why the restore of a rollback failed; the rollback stays, to be restored at the next start.</summary>
    /// <param name="error">Why, for a person.</param>
    /// <returns>The state.</returns>
    public UpdateState RecordRestoreFailure(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return Change(state => state.RollingBack is not { } rollback ? state : state with { RollingBack = rollback with { Error = error } });
    }

    /// <summary>Ends a rollback once its backup is restored.</summary>
    /// <param name="trial">The trial of the rollback restored.</param>
    /// <returns>Whether it was the rollback that waited.</returns>
    public bool CompleteRollback(UpdateTrial trial)
    {
        ArgumentNullException.ThrowIfNull(trial);
        var completed = false;
        Change(state =>
        {
            if (state.RollingBack is not { } rollback || !string.Equals(rollback.Trial.Version, trial.Version, StringComparison.OrdinalIgnoreCase)
                || rollback.Trial.SelectedAt != trial.SelectedAt) return state;
            completed = true;
            return state with { RollingBack = null };
        });
        return completed;
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
