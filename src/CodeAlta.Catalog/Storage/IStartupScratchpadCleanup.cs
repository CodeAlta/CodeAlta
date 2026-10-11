using System.Collections.Immutable;

namespace CodeAlta.Catalog.Storage;

/// <summary>The lifecycle of the instance's one startup scratchpad pass.</summary>
public enum ScratchpadCleanupState
{
    /// <summary>The host has not started the pass.</summary>
    NotStarted,
    /// <summary>The actual filesystem worker is running.</summary>
    Running,
    /// <summary>All ordinary scratchpad contents were removed, or the root was absent.</summary>
    Completed,
    /// <summary>Some entries could not safely be removed.</summary>
    Partial,
    /// <summary>A configuration or safety gate prevented cleanup.</summary>
    Skipped,
    /// <summary>Cancellation stopped the worker; earlier removals are not undone.</summary>
    Canceled,
}

/// <summary>An immutable, replayable result of startup scratchpad maintenance.</summary>
/// <param name="State">The actual worker lifecycle, not an admission waiter's state.</param>
/// <param name="RemovedFiles">Successfully removed files.</param>
/// <param name="RemovedDirectories">Successfully removed child directories; the root is preserved.</param>
/// <param name="SkippedEntries">Entries left in place because a safety or I/O check failed.</param>
/// <param name="Reasons">Bounded diagnostic codes, never unbounded exception messages.</param>
/// <param name="StartedAt">UTC start time, when started.</param>
/// <param name="CompletedAt">UTC termination time, when settled.</param>
public sealed record ScratchpadCleanupSnapshot(ScratchpadCleanupState State, long RemovedFiles,
    long RemovedDirectories, long SkippedEntries, ImmutableArray<string> Reasons,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

/// <summary>The host admission and presentation contract for instance-owned scratchpad maintenance.</summary>
/// <remarks>Host composition supplies the implementation; orchestration does not acquire instance locks.</remarks>
public interface IStartupScratchpadCleanup
{
    /// <summary>Gets the latest snapshot, including for late subscribers.</summary>
    ScratchpadCleanupSnapshot Snapshot { get; }
    /// <summary>Gets the captured instance root; host composition must match it.</summary>
    string StateRoot { get; }
    /// <summary>Gets the captured shared resource root.</summary>
    string GlobalRoot { get; }
    /// <summary>Subscribes and immediately replays current status; disposal detaches, never cancels.</summary>
    /// <exception cref="ArgumentNullException">The observer is null.</exception>
    IDisposable Subscribe(Action<ScratchpadCleanupSnapshot> observer);
    /// <summary>Starts once off-thread and joins the actual worker, including on cancellation.</summary>
    /// <remarks>Repeated callers join that original operation; they cannot start another pass.</remarks>
    Task RunAsync(bool enabled, bool configurationValid, CancellationToken cancellationToken);
}
