using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

// Infrastructure-only state: no bindings or UI callbacks run on the owned work chain.
internal sealed class SessionPromptDraftPersistenceCoordinator : IAsyncDisposable
{
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.UI");
    private readonly PromptDraftStore _store;
    private readonly Func<string, string?, TextFileRevision, Task<PromptDraftSaveResult>> _save;
    private readonly TimeSpan _saveDelay;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, DraftState> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private Task _work = Task.CompletedTask;
    private bool _disposed;

    public SessionPromptDraftPersistenceCoordinator(CatalogOptions catalogOptions, TimeSpan? saveDelay = null)
        : this(new PromptDraftStore(catalogOptions), saveDelay ?? TimeSpan.FromMilliseconds(500))
    {
    }

    internal SessionPromptDraftPersistenceCoordinator(
        PromptDraftStore store,
        TimeSpan saveDelay,
        Func<string, string?, TextFileRevision, Task<PromptDraftSaveResult>>? save = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (saveDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(saveDelay));
        }

        _store = store;
        _save = save ?? store.SaveAsync;
        _saveDelay = saveDelay;
    }

    public string? LoadPromptDraft(string scopeKey)
    {
        lock (_syncRoot)
        {
            var draft = GetDraft(scopeKey);
            if (draft.Revision is null && !draft.IsDirty)
            {
                throw new IOException("Failed to load prompt draft.", draft.Error);
            }

            return draft.Text;
        }
    }

    public bool HasPromptDraft(string scopeKey)
    {
        lock (_syncRoot)
        {
            var draft = GetDraft(scopeKey);
            if (draft.Revision is null && !draft.IsDirty)
            {
                throw new IOException("Failed to load prompt draft.", draft.Error);
            }

            return draft.IsDirty ? !string.IsNullOrWhiteSpace(draft.Text) : draft.Revision?.Exists == true;
        }
    }

    public void ObservePromptDraft(string scopeKey, string? promptText)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var draft = GetDraft(scopeKey);
            draft.Text = promptText;
            draft.Version++;
            draft.Delay?.Cancel();
            var delay = new CancellationTokenSource();
            draft.Delay = delay;
            _work = PersistAsync(_work, scopeKey, draft, draft.Version, delay);
        }
    }

    // Existing synchronous deletion callers get an acknowledgement, not cancellation-as-success.
    public void DeletePromptDraft(string scopeKey)
    {
        ObservePromptDraft(scopeKey, null);
        FlushAsync().GetAwaiter().GetResult().ThrowIfFailed();
    }

    public Task<PromptDraftFlushResult> FlushAsync()
    {
        lock (_syncRoot)
        {
            return QueueFlush();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<PromptDraftFlushResult> flush;
        lock (_syncRoot)
        {
            _disposed = true; // Stop admission before capturing/joining all owned work, including deletes.
            flush = QueueFlush();
        }

        (await flush.ConfigureAwait(false)).ThrowIfFailed();
    }

    private DraftState GetDraft(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (_drafts.TryGetValue(scopeKey, out var draft))
        {
            return draft;
        }

        draft = new DraftState();
        try
        {
            var loaded = _store.Load(scopeKey);
            draft.Text = loaded.Text;
            draft.Revision = loaded.Revision;
        }
        catch (Exception ex)
        {
            draft.Error = ex; // Without a baseline, never guess that the file was missing.
        }

        _drafts.Add(scopeKey, draft);
        return draft;
    }

    // Called under _syncRoot. A flush covers the edits admitted at this point, not future edits.
    private Task<PromptDraftFlushResult> QueueFlush()
    {
        var targets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, draft) in _drafts)
        {
            if (!draft.IsDirty)
            {
                continue;
            }

            targets.Add(key, draft.Version);
            draft.Delay?.Cancel();
            _work = PersistAsync(_work, key, draft, draft.Version, delay: null);
        }

        return CompleteFlushAsync(_work, targets);
    }

    private async Task<PromptDraftFlushResult> CompleteFlushAsync(Task work, Dictionary<string, long> targets)
    {
        await work.ConfigureAwait(false);
        lock (_syncRoot)
        {
            var failures = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, version) in targets)
            {
                var draft = _drafts[key];
                if (draft.AcknowledgedVersion < version)
                {
                    failures.Add(key, draft.Error ?? new IOException("A newer prompt edit is still pending."));
                }
            }

            return new PromptDraftFlushResult(failures);
        }
    }

    private async Task PersistAsync(Task previous, string key, DraftState draft, long version, CancellationTokenSource? delay)
    {
        try
        {
            await JoinDraftPrerequisitesAsync(
                previous,
                delay is null ? static () => Task.CompletedTask : () => Task.Delay(_saveDelay, delay.Token),
                delay is null ? static () => false : () => delay.IsCancellationRequested).ConfigureAwait(false);
            string? text;
            TextFileRevision revision;
            lock (_syncRoot)
            {
                if (delay?.IsCancellationRequested == true || version != draft.Version || !draft.IsDirty)
                {
                    return;
                }

                if (draft.Revision is null)
                {
                    return;
                }

                text = draft.Text;
                revision = draft.Revision;
            }

            // Once a write starts it is joined, never canceled to pretend it did not commit.
            // The following operation uses only this operation's acknowledged revision.
            var result = await _save(key, text, revision).ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (result.IsConflict)
                {
                    draft.Error = new IOException($"Prompt draft '{key}' changed on disk; pending text was retained.");
                    _logger.Error(draft.Error, "Prompt draft persistence conflict.");
                    return; // Never adopt the conflicting revision and silently retry.
                }

                draft.Revision = result.Snapshot.Revision;
                draft.AcknowledgedVersion = version;
                draft.Error = null;
                if (draft.Version == version)
                {
                    draft.Text = result.Snapshot.Text;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_syncRoot)
            {
                draft.Error = ex;
            }

            _logger.Error(ex, $"Failed to persist prompt draft '{key}'; pending text was retained.");
        }
        finally
        {
            if (delay is not null)
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(draft.Delay, delay))
                    {
                        draft.Delay = null;
                    }

                    delay.Dispose();
                }
            }
        }
    }

    private sealed class DraftState
    {
        public string? Text { get; set; }
        public TextFileRevision? Revision { get; set; }
        public long Version { get; set; }
        public long AcknowledgedVersion { get; set; }
        public bool IsDirty => Version != AcknowledgedVersion;
        public Exception? Error { get; set; }
        public CancellationTokenSource? Delay { get; set; }
    }

    /// <summary>
    /// Joins the original delay and previous prompt-draft prerequisites without owning their resources.
    /// </summary>
    /// <remarks>
    /// All mandatory arguments receive synchronous validation in signature order before the local core
    /// starts inline. Retain previous before invoking the delay factory once, retain its original task,
    /// and treat a returned null task as a seam-contract failure, not concrete Task.Delay behavior.
    /// The delay-only OCE filter evaluates the predicate once only for delay acquisition or await OCE:
    /// true suppresses, false retains; a throwing predicate is filter false under C# semantics and retains
    /// the original OCE, not a new predicate error. Never suppress or classify previous cancellation.
    /// After every terminal delay outcome, including acquisition/null failure, independently await previous.
    /// A lone original error uses EDI; an aggregate contains direct ordered [delay, previous] errors,
    /// preserving nested and repeated references without flatten or dedup operations.
    /// This intentionally changes exceptional ordering: error storage, logging and local source release
    /// now wait for previous after terminal delay failure, and both errors reach the unchanged outer catch.
    /// That outer catch ordinarily stores/logs rather than propagates; logger/finally precedence is unchanged.
    /// Awaits suppress infrastructure context capture, not a forced thread switch for completed tasks;
    /// the frontend plain-await caller retains its own context boundary. Pending delay noncompletion
    /// prevents reaching previous, and pending previous blocks completion. No timeout or termination is added.
    /// Only supplied original tasks are joined: hidden descendants and adapter/invocation setup are excluded.
    /// Observe setup, partial flush retention in QueueFlush, reentrant admission, concurrent delete/edit, UI rollback
    /// and callback invalidation remain open. This is not complete draft shutdown or broader owner cleanup.
    /// </remarks>
    /// <param name="previous">The mandatory original predecessor task, retained and independently joined.</param>
    /// <param name="waitDelay">The mandatory factory invoked once inline to acquire the original delay task.</param>
    /// <param name="isDelayCancellationRequested">The mandatory delay-only cancellation filter predicate.</param>
    /// <exception cref="ArgumentNullException">A mandatory argument is null; thrown synchronously in signature order.</exception>
    /// <exception cref="InvalidOperationException">The delay operation returned a null task; joined with any previous failure.</exception>
    /// <exception cref="OperationCanceledException">The lone retained failure is an unsuppressed delay or previous OCE.</exception>
    /// <exception cref="Exception">A lone original failure is rethrown through EDI after both prerequisites terminate.</exception>
    /// <exception cref="AggregateException">Multiple direct original failures remain ordered without flattening or deduplication.</exception>
    internal static Task JoinDraftPrerequisitesAsync(
        Task previous,
        Func<Task> waitDelay,
        Func<bool> isDelayCancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(waitDelay);
        ArgumentNullException.ThrowIfNull(isDelayCancellationRequested);
        return CoreAsync();

        async Task CoreAsync()
        {
            List<Exception>? failures = null;
            try
            {
                var delayTask = waitDelay();
                if (delayTask is null)
                {
                    throw new InvalidOperationException("The delay operation returned a null task.");
                }

                await delayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (isDelayCancellationRequested())
            {
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is { Count: 1 })
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }
    }
}

internal sealed record PromptDraftFlushResult(IReadOnlyDictionary<string, Exception> Failures)
{
    public bool Succeeded => Failures.Count == 0;

    public void ThrowIfFailed()
    {
        if (!Succeeded)
        {
            throw new IOException("Prompt drafts could not be flushed; pending text was retained.", new AggregateException(Failures.Values));
        }
    }
}
