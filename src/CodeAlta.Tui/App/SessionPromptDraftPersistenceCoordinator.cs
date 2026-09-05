using CodeAlta.Catalog;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

internal sealed class SessionPromptDraftPersistenceCoordinator : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.UI");
    private readonly string _promptDraftsRoot;
    private readonly TimeSpan _saveDelay;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, PendingPromptDraftSave> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);

    public SessionPromptDraftPersistenceCoordinator(CatalogOptions catalogOptions, TimeSpan? saveDelay = null)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        if (string.IsNullOrWhiteSpace(catalogOptions.GlobalRoot))
        {
            throw new ArgumentException("Global root is required.", nameof(catalogOptions));
        }

        _promptDraftsRoot = catalogOptions.PromptDraftsRoot;
        _saveDelay = saveDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public string? LoadPromptDraft(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(sessionId, out var pending))
            {
                return pending.PromptText;
            }
        }

        var path = GetPromptDraftPath(sessionId);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : null;
    }

    public bool HasPromptDraft(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(sessionId, out var pending))
            {
                return !string.IsNullOrWhiteSpace(pending.PromptText);
            }
        }

        return File.Exists(GetPromptDraftPath(sessionId));
    }

    public void ObservePromptDraft(string sessionId, string? promptText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var normalizedPrompt = promptText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            CancelPendingSave(sessionId);
            DeletePromptDraft(sessionId);
            return;
        }

        var cancellationSource = new CancellationTokenSource();
        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(sessionId, out var existing))
            {
                existing.CancellationSource.Cancel();
                existing.CancellationSource.Dispose();
            }

            _pendingSaves[sessionId] = new PendingPromptDraftSave(normalizedPrompt, cancellationSource);
        }

        _ = PersistPromptDraftAsync(sessionId, normalizedPrompt, cancellationSource);
    }

    public void DeletePromptDraft(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        CancelPendingSave(sessionId);

        try
        {
            var path = GetPromptDraftPath(sessionId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"Failed to delete saved prompt draft for '{sessionId}'.");
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

    private async Task PersistPromptDraftAsync(string sessionId, string promptText, CancellationTokenSource cancellationSource)
    {
        try
        {
            await Task.Delay(_saveDelay, cancellationSource.Token).ConfigureAwait(false);
            await WritePromptDraftAsync(sessionId, promptText, cancellationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailure(ex, $"Failed to persist saved prompt draft for '{sessionId}'.");
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_pendingSaves.TryGetValue(sessionId, out var pending) &&
                    ReferenceEquals(pending.CancellationSource, cancellationSource))
                {
                    _pendingSaves.Remove(sessionId);
                }
            }

            cancellationSource.Dispose();
        }
    }

    private async Task WritePromptDraftAsync(string sessionId, string promptText, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_promptDraftsRoot);
        var path = GetPromptDraftPath(sessionId);
        await File.WriteAllTextAsync(path, promptText, cancellationToken).ConfigureAwait(false);
    }

    private void CancelPendingSave(string sessionId)
    {
        CancellationTokenSource? pendingCancellation = null;
        lock (_syncRoot)
        {
            if (_pendingSaves.TryGetValue(sessionId, out var pending))
            {
                pendingCancellation = pending.CancellationSource;
                _pendingSaves.Remove(sessionId);
            }
        }

        if (pendingCancellation is null)
        {
            return;
        }

        pendingCancellation.Cancel();
        pendingCancellation.Dispose();
    }

    private string GetPromptDraftPath(string sessionId)
        => Path.Combine(_promptDraftsRoot, $"saved_prompt_{SanitizeFileName(sessionId)}.md");

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
        Logger.Error(ex, message);
    }

    private sealed record PendingPromptDraftSave(string PromptText, CancellationTokenSource CancellationSource);
}
