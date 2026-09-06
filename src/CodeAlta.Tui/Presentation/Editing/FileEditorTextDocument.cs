using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Tui.Presentation.Editing;

// The pinned sealed CodeEditor has no read-only option. Keep its selection/search/copy
// engine and original document/undo identity; filter mutations at its document port.
// This is UI behavior only: Catalog separately guards every workflow save.
internal sealed class FileEditorTextDocument(string text, Func<bool> isReadOnly) : ITextDocument
{
    internal TextDocument Content { get; } = new(text);
    public ITextSnapshot CurrentSnapshot => Content.CurrentSnapshot;
    public int Version => Content.Version;
    public IDisposable BeginUpdate() => Content.BeginUpdate();
    public event EventHandler<TextDocumentChangedEventArgs> Changed
    {
        add => Content.Changed += value;
        remove => Content.Changed -= value;
    }
    public void Insert(int position, ReadOnlySpan<char> text)
    {
        if (!isReadOnly()) Content.Insert(position, text);
    }
    public void Remove(int position, int length)
    {
        if (!isReadOnly()) Content.Remove(position, length);
    }
    public void Replace(int position, int length, ReadOnlySpan<char> text)
    {
        if (!isReadOnly()) Content.Replace(position, length, text);
    }
}
