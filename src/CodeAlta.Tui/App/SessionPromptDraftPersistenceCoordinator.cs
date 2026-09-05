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
            if (delay is not null)
            {
                try
                {
                    await Task.Delay(_saveDelay, delay.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (delay.IsCancellationRequested)
                {
                }
            }

            await previous.ConfigureAwait(false);
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
