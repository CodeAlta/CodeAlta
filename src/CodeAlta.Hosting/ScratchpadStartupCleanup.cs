using System.Collections.Immutable;
using System.Diagnostics;
using CodeAlta.Catalog.Storage;

namespace CodeAlta.Hosting;

/// <summary>Owns one background cleanup pass and the barrier preceding runnable host construction.</summary>
/// <remarks>
/// Only trusted host composition supplies the profile and its acquired instance guard. Cancellation
/// requests stop between filesystem operations, but every caller joins the original worker. There
/// is no lifetime lease shared by normal and developer hosts. Windows removes through pinned
/// handles; macOS and Linux (x64, arm64) remove relative to directory descriptors, checking the
/// identity of each entry. Other platforms report a skipped result instead of falling back to
/// check-then-delete paths.
/// </remarks>
public sealed class ScratchpadStartupCleanup : IStartupScratchpadCleanup
{
    private readonly object _gate = new();
    private readonly CodeAltaInstanceProfile _profile;
    private readonly CodeAltaSingleInstanceGuard? _ownership;
    private readonly List<Action<ScratchpadCleanupSnapshot>> _subscribers = [];
    private ScratchpadCleanupSnapshot _snapshot = new(ScratchpadCleanupState.NotStarted, 0, 0, 0, [], null, null);
    private Task? _worker;

    /// <summary>Captures trusted instance paths and ownership; missing ownership fails closed.</summary>
    /// <exception cref="ArgumentNullException">The profile is null.</exception>
    public ScratchpadStartupCleanup(CodeAltaInstanceProfile profile, CodeAltaSingleInstanceGuard? ownership)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _ownership = ownership;
    }

    /// <summary>Gets the latest snapshot, also available to late Storage/toast subscribers.</summary>
    public ScratchpadCleanupSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Gets the captured instance state root; host composition must match it before starting.</summary>
    public string StateRoot => _profile.StateRoot;

    /// <summary>Gets the shared resource root, which is not the developer scratchpad root.</summary>
    public string GlobalRoot => _profile.GlobalRoot;

    /// <summary>Gets how long a scratchpad entry is kept after it was created or last written.</summary>
    internal TimeSpan MaxAge { get; init; } = StorageSettings.ScratchpadMaxAge;

    internal Action<CancellationToken>? WorkerStarted { get; init; }
    internal Action<string>? BeforeEntryOpen { get; init; }
    internal Action<string>? BeforeEntryDelete { get; init; }
    // Unix only: runs after a file was renamed to its claim name, before what the claim moved is checked.
    internal Action<string>? AfterEntryClaim { get; init; }
    // The clock that sets the start of the pass, from which the age of the entries counts.
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Subscribes and immediately replays the current snapshot in notification order.</summary>
    /// <returns>A subscription that the UI detaches when it closes; detaching never cancels maintenance.</returns>
    /// <exception cref="ArgumentNullException">The observer is null.</exception>
    /// <remarks>Observers must return promptly. Their exceptions do not fail cleanup or startup.</remarks>
    public IDisposable Subscribe(Action<ScratchpadCleanupSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _subscribers.Add(observer);
            Notify(observer, _snapshot);
        }
        return new Subscription(this, observer);
    }

    /// <summary>Starts once, off-thread, and joins the original worker even when cancellation is requested.</summary>
    /// <param name="enabled">The resolved scratchpad preference; absent configuration means true.</param>
    /// <param name="configurationValid">False prevents a malformed setting from becoming a destructive default.</param>
    /// <param name="cancellationToken">Requests cancellation of this pass. Later callers only join it.</param>
    /// <returns>The actual worker termination, never a cancelable proxy wait.</returns>
    public Task RunAsync(bool enabled, bool configurationValid, CancellationToken cancellationToken)
    {
        lock (_gate)
            return _worker ??= Task.Run(() => Run(enabled, configurationValid, cancellationToken), CancellationToken.None);
    }

    private void Run(bool enabled, bool configurationValid, CancellationToken cancellationToken)
    {
        var started = Time.GetUtcNow();
        var result = new ScratchpadRemovalResult
        {
            Progress = current => Publish(new(ScratchpadCleanupState.Running, current.RemovedFiles, current.RemovedDirectories,
                current.SkippedEntries, current.Reasons.ToImmutableArray(), started, null)
            { ProcessedFiles = current.ProcessedFiles, ProcessedDirectories = current.ProcessedDirectories, RemovedBytes = current.RemovedBytes }),
        };
        var state = ScratchpadCleanupState.Skipped;
        try
        {
            if (!configurationValid) result.Reason("invalid-configuration");
            else if (!enabled) result.Reason("disabled");
            else if (!HasPathSafety) result.Reason("platform-path-safety-unavailable");
            else if (_ownership is null || !string.Equals(_ownership.LockFilePath, _profile.LockFilePath, StringComparison.OrdinalIgnoreCase))
                result.Reason("instance-ownership-unavailable");
            else
            {
                // The hold keeps the real lock open and its file in place even if a caller disposes its
                // guard prematurely: the guard releases both when the hold ends. On Windows its FileShare.Read prevents pathname replacement throughout
                // this pass; on Unix the removal proves that the state root holds this very file.
                using var ownership = _ownership.HoldForScratchpadCleanup();
                Publish(new(ScratchpadCleanupState.Running, 0, 0, 0, [], started, null));
                WorkerStarted?.Invoke(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                state = Clear(ownership.Handle, started - MaxAge, result, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state = ScratchpadCleanupState.Canceled;
            result.Reason("canceled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            state = result.RemovedFiles + result.RemovedDirectories == 0 ? ScratchpadCleanupState.Skipped : ScratchpadCleanupState.Partial;
            result.Reason(ex is ScratchpadSafetyException safety ? safety.Reason : "filesystem-or-ownership-unavailable");
        }
        catch (Exception)
        {
            // Maintenance is best effort. An unexpected managed failure still joins/unwinds the
            // actual worker and releases its exact handles before admitting a runnable host.
            state = result.RemovedFiles + result.RemovedDirectories == 0 ? ScratchpadCleanupState.Skipped : ScratchpadCleanupState.Partial;
            result.Reason("unexpected-cleanup-failure");
        }
        finally
        {
            Publish(new(state, result.RemovedFiles, result.RemovedDirectories, result.SkippedEntries,
                result.Reasons.ToImmutableArray(), started, Time.GetUtcNow())
            { ProcessedFiles = result.ProcessedFiles, ProcessedDirectories = result.ProcessedDirectories, RemovedBytes = result.RemovedBytes });
        }
    }

    private static bool HasPathSafety
        => OperatingSystem.IsWindows() || ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && UnixScratchpadRemoval.IsSupported);

    private ScratchpadCleanupState Clear(Microsoft.Win32.SafeHandles.SafeFileHandle ownership, DateTimeOffset cutoff,
        ScratchpadRemovalResult result, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return WindowsScratchpadRemoval.Clear(_profile, ownership, cutoff, result, BeforeEntryOpen, BeforeEntryDelete, cancellationToken);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return UnixScratchpadRemoval.Clear(_profile, ownership, cutoff, result, BeforeEntryOpen, BeforeEntryDelete, AfterEntryClaim, cancellationToken);
        throw new ScratchpadSafetyException("platform-path-safety-unavailable");
    }

    private void Publish(ScratchpadCleanupSnapshot snapshot)
    {
        lock (_gate)
        {
            Volatile.Write(ref _snapshot, snapshot);
            // A callback may detach itself.
            foreach (var subscriber in _subscribers.ToArray()) Notify(subscriber, snapshot);
        }
    }

    private static void Notify(Action<ScratchpadCleanupSnapshot> observer, ScratchpadCleanupSnapshot snapshot)
    {
        try { observer(snapshot); }
        catch { /* Presentation observers must not break the ownership barrier. */ }
    }

    private sealed class Subscription(ScratchpadStartupCleanup owner, Action<ScratchpadCleanupSnapshot> observer) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._subscribers.Remove(observer); }
    }
}

internal sealed class ScratchpadRemovalResult
{
    private long _lastProgress;
    internal Action<ScratchpadRemovalResult>? Progress { get; init; }
    internal long ProcessedFiles;
    internal long ProcessedDirectories;
    internal long RemovedFiles;
    internal long RemovedDirectories;
    internal long RemovedBytes;
    internal long SkippedEntries;
    internal List<string> Reasons { get; } = [];
    internal void Reason(string reason)
    {
        if (Reasons.Count < 12 && !Reasons.Contains(reason, StringComparer.Ordinal)) Reasons.Add(reason);
    }
    internal void Skip(string reason) { SkippedEntries++; Reason(reason); }

    internal void ReportProgress()
    {
        if (_lastProgress != 0 && Stopwatch.GetElapsedTime(_lastProgress).TotalMilliseconds < 250) return;
        Progress?.Invoke(this);
        _lastProgress = Stopwatch.GetTimestamp();
    }
}

internal sealed class ScratchpadSafetyException(string reason) : IOException(reason)
{
    internal string Reason { get; } = reason;
}
