using CodeAlta.Catalog;

namespace CodeAlta.Tui.Views;

// Synchronous adapter for the dialog's synchronous commands. The shared codec's awaited I/O
// stays below this boundary; failed operations never acknowledge or invoke the reload callback.
internal sealed class PromptEditingSession(PromptResourceStore store, PromptResourceSnapshot snapshot)
{
    public PromptResourceSnapshot Snapshot { get; private set; } = snapshot;
    public TextFileRevision? ConflictRevision { get; private set; }

    public bool Save(PromptFileContent content, Action onSuccess, Action<string> onFailure, TextFileRevision? confirmedRevision = null)
    {
        ConflictRevision = null;
        TextFileSaveResult result;
        try
        {
            result = store.Save(Snapshot, content, confirmedRevision ?? Snapshot.File.Revision);
            if (result.IsConflict)
            {
                ConflictRevision = result.CurrentRevision;
                onFailure(SR.T("The prompt file changed outside this dialog. Your edits have been retained."));
                return false;
            }
            Snapshot = store.Acknowledge(Snapshot.Identity, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            onFailure(ex.Message);
            return false;
        }
        onSuccess();
        return true;
    }

    public bool Delete(Action onSuccess, Action<string> onFailure, TextFileRevision? confirmedRevision = null)
    {
        ConflictRevision = null;
        try
        {
            var result = store.Delete(Snapshot, confirmedRevision ?? Snapshot.File.Revision);
            if (result.IsConflict)
            {
                ConflictRevision = result.CurrentRevision;
                onFailure(SR.T("The prompt file changed outside this dialog. Your edits have been retained."));
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            onFailure(ex.Message);
            return false;
        }
        onSuccess();
        return true;
    }
}
