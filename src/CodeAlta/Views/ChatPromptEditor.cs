using CodeAlta.Presentation.Prompting;
using CodeAlta.Search;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal sealed class ChatPromptEditor : PromptEditor, IProjectFileReferencePopupHost
{
    private readonly Action<string> _onAccepted;
    private readonly Action? _onOpenHelp;
    private readonly Action? _onOpenCommandPalette;
    private ProjectFileReferencePopupController? _projectFileReferencePopupController;

    public ChatPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp = null,
        Action? onOpenCommandPalette = null)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        _onAccepted = onAccepted;
        _onOpenHelp = onOpenHelp;
        _onOpenCommandPalette = onOpenCommandPalette;
    }

    protected override void OnAccepted(PromptEditorAcceptedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _ = _projectFileReferencePopupController?.DisposeAsync();
        _onAccepted(e.Text);
        base.OnAccepted(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.Handled && TryHandleTransientShortcutInput(e.Text))
        {
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
        _projectFileReferencePopupController?.HandleEditorStateChanged();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.Handled &&
            OperatingSystem.IsWindows() &&
            EnterMode == PromptEditorEnterMode.EnterInsertsNewLine &&
            e.Key == TerminalKey.Enter &&
            (e.Modifiers & TerminalModifiers.Ctrl) != 0)
        {
            Accept();
            e.Handled = true;
            _projectFileReferencePopupController?.HandleEditorStateChanged();
            return;
        }

        base.OnKeyDown(e);
        _projectFileReferencePopupController?.HandleEditorStateChanged();
    }

    internal bool TryHandleTransientShortcutInput(string? text)
    {
        if (TextDocument.CurrentSnapshot.Length != 0 || CaretIndex != 0 || SelectionLength != 0)
        {
            return false;
        }

        return text switch
        {
            "?" when _onOpenHelp is not null => InvokeTransientShortcut(_onOpenHelp),
            "/" when _onOpenCommandPalette is not null => InvokeTransientShortcut(_onOpenCommandPalette),
            _ => false,
        };
    }

    private static bool InvokeTransientShortcut(Action action)
    {
        action();
        return true;
    }

    public ChatPromptEditor EnableProjectFileReferences(
        IProjectFileSearchService searchService,
        IProjectFileAppearanceRegistry appearanceRegistry,
        Func<string?> getProjectRoot)
    {
        ArgumentNullException.ThrowIfNull(searchService);
        ArgumentNullException.ThrowIfNull(appearanceRegistry);
        ArgumentNullException.ThrowIfNull(getProjectRoot);

        _ = _projectFileReferencePopupController?.DisposeAsync();
        _projectFileReferencePopupController = new ProjectFileReferencePopupController(
            this,
            searchService,
            appearanceRegistry,
            getProjectRoot);
        return this;
    }

    internal bool HasProjectFileReferencePopup => _projectFileReferencePopupController?.IsOpen == true;

    internal IReadOnlyList<ProjectFileReferencePopupItem> ProjectFileReferenceItems
        => _projectFileReferencePopupController?.Items ?? [];

    internal int ProjectFileReferenceSelectedIndex
        => _projectFileReferencePopupController?.SelectedIndex ?? -1;

    internal string ProjectFileReferenceQueryText
        => _projectFileReferencePopupController?.QueryText ?? string.Empty;

    internal void RefreshProjectFileReferencePopup()
        => _projectFileReferencePopupController?.HandleEditorStateChanged();

    internal void InsertTextAtSelection(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        var selectionStart = SelectionStart;
        TextDocument.Replace(selectionStart, SelectionLength, text.AsSpan());
        CaretIndex = selectionStart + text.Length;
        _projectFileReferencePopupController?.HandleEditorStateChanged();
    }

    Visual IProjectFileReferencePopupHost.Visual => this;

    void IProjectFileReferencePopupHost.FocusPromptEditor()
        => App?.Focus(this);
}
