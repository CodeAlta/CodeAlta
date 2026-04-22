namespace CodeAlta.Presentation.Editing;

internal sealed class FileEditorSessionState
{
    private string _savedText;
    private DateTimeOffset _savedWriteTimeUtc;

    public FileEditorSessionState(string savedText, DateTimeOffset savedWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(savedText);

        _savedText = savedText;
        _savedWriteTimeUtc = savedWriteTimeUtc;
        ExistsOnDisk = true;
    }

    public bool IsDirty { get; private set; }

    public bool HasExternalChanges { get; private set; }

    public bool ExistsOnDisk { get; private set; }

    public DateTimeOffset SavedWriteTimeUtc => _savedWriteTimeUtc;

    public void UpdateEditorText(string currentText)
    {
        ArgumentNullException.ThrowIfNull(currentText);
        IsDirty = !string.Equals(currentText, _savedText, StringComparison.Ordinal);
    }

    public void MarkSaved(string currentText, DateTimeOffset savedWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(currentText);

        _savedText = currentText;
        _savedWriteTimeUtc = savedWriteTimeUtc;
        IsDirty = false;
        HasExternalChanges = false;
        ExistsOnDisk = true;
    }

    public void MarkReloaded(string text, DateTimeOffset savedWriteTimeUtc)
        => MarkSaved(text, savedWriteTimeUtc);

    public void RefreshExternalState(bool existsOnDisk, DateTimeOffset? currentWriteTimeUtc)
    {
        ExistsOnDisk = existsOnDisk;
        HasExternalChanges = !existsOnDisk || (currentWriteTimeUtc.HasValue && currentWriteTimeUtc.Value != _savedWriteTimeUtc);
    }
}
