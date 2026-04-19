using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class ThreadPromptDraftPersistenceCoordinator : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.UI");
    private readonly string _promptDraftsRoot;
    private readonly TimeSpan _saveDelay;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, PendingPromptDraftSave> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);

    public ThreadPromptDraftPersistenceCoordinator(CatalogOptions catalogOptions, TimeSpan? saveDelay = null)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        if (string.IsNullOrWhiteSpace(catalogOptions.GlobalRoot))
        {
            throw new ArgumentException("Global root is required.", nameof(catalogOptions));
        }

        _promptDraftsRoot = catalogOptions.PromptDraftsRoot;
        _saveDelay = saveDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public string? LoadPromptDraft(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(threadId, out var pending))
            {
                return pending.PromptText;
            }
        }

        var path = GetPromptDraftPath(threadId);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : null;
    }

    public bool HasPromptDraft(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(threadId, out var pending))
            {
                return !string.IsNullOrWhiteSpace(pending.PromptText);
            }
        }

        return File.Exists(GetPromptDraftPath(threadId));
    }

    public void ObservePromptDraft(string threadId, string? promptText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var normalizedPrompt = promptText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            CancelPendingSave(threadId);
            DeletePromptDraft(threadId);
            return;
        }

        var cancellationSource = new CancellationTokenSource();
        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(threadId, out var existing))
            {
                existing.CancellationSource.Cancel();
                existing.CancellationSource.Dispose();
            }

            _pendingSaves[threadId] = new PendingPromptDraftSave(normalizedPrompt, cancellationSource);
        }

        _ = PersistPromptDraftAsync(threadId, normalizedPrompt, cancellationSource);
    }

    public void DeletePromptDraft(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        CancelPendingSave(threadId);

        try
        {
            var path = GetPromptDraftPath(threadId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"Failed to delete saved prompt draft for '{threadId}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<KeyValuePair<string, string>> pendingWrites;
        lock (_syncRoot)
        {
            pendingWrites = _pendingSaves
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Value.PromptText))
                .Select(static entry => new KeyValuePair<string, string>(entry.Key, entry.Value.PromptText))
                .ToList();

            foreach (var pending in _pendingSaves.Values)
            {
                pending.CancellationSource.Cancel();
                pending.CancellationSource.Dispose();
            }

            _pendingSaves.Clear();
        }

        foreach (var pendingWrite in pendingWrites)
        {
            try
            {
                await WritePromptDraftAsync(pendingWrite.Key, pendingWrite.Value, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure(ex, $"Failed to flush saved prompt draft for '{pendingWrite.Key}'.");
            }
        }
    }

    private async Task PersistPromptDraftAsync(string threadId, string promptText, CancellationTokenSource cancellationSource)
    {
        try
        {
            await Task.Delay(_saveDelay, cancellationSource.Token).ConfigureAwait(false);
            await WritePromptDraftAsync(threadId, promptText, cancellationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"Failed to persist saved prompt draft for '{threadId}'.");
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_pendingSaves.TryGetValue(threadId, out var pending) &&
                    ReferenceEquals(pending.CancellationSource, cancellationSource))
                {
                    _pendingSaves.Remove(threadId);
                }
            }

            cancellationSource.Dispose();
        }
    }

    private async Task WritePromptDraftAsync(string threadId, string promptText, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_promptDraftsRoot);
        var path = GetPromptDraftPath(threadId);
        await File.WriteAllTextAsync(path, promptText, cancellationToken).ConfigureAwait(false);
    }

    private void CancelPendingSave(string threadId)
    {
        CancellationTokenSource? pendingCancellation = null;
        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(threadId, out var pending))
            {
                pendingCancellation = pending.CancellationSource;
                _pendingSaves.Remove(threadId);
            }
        }

        if (pendingCancellation is null)
        {
            return;
        }

        pendingCancellation.Cancel();
        pendingCancellation.Dispose();
    }

    private string GetPromptDraftPath(string threadId)
        => Path.Combine(_promptDraftsRoot, $"saved_prompt_{SanitizeFileName(threadId)}.md");

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            builder[i] = invalidCharacters.Contains(value[i]) ? '-' : value[i];
        }

        return new string(builder);
    }

    private static void LogFailure(Exception ex, string message)
    {
        if (LogManager.IsInitialized && Logger.IsEnabled(LogLevel.Error))
        {
            Logger.Error(ex, message);
        }
    }

    private sealed record PendingPromptDraftSave(string PromptText, CancellationTokenSource CancellationSource);
}
