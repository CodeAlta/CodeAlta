using CodeAlta.Presentation.Prompting;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal sealed class ChatPromptEditor : PromptEditor, IProjectFileReferencePopupHost, IPluginPromptEditorHost
{
    private readonly Action<string> _onAccepted;
    private readonly Action? _onOpenHelp;
    private readonly Action? _onOpenCommandPalette;
    private ProjectFileReferencePopupController? _projectFileReferencePopupController;
    private Func<string?>? _getPromptReferenceProjectRoot;
    private readonly List<IAsyncDisposable> _promptEditorAttachments = [];

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
        Accepted?.Invoke(this, EventArgs.Empty);
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
        EditorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnKeyDown(e);
        _projectFileReferencePopupController?.HandleEditorStateChanged();
        EditorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? EditorStateChanged;

    public event EventHandler? Accepted;

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
        _getPromptReferenceProjectRoot = getProjectRoot;
        _projectFileReferencePopupController = new ProjectFileReferencePopupController(
            this,
            searchService,
            appearanceRegistry,
            getProjectRoot);
        return this;
    }

    internal bool HasProjectFileReferencePopup => _projectFileReferencePopupController?.IsOpen == true;

    public ChatPromptEditor EnablePromptEditorContributions(IReadOnlyList<PluginPromptEditorContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        foreach (var attachment in _promptEditorAttachments)
        {
            _ = attachment.DisposeAsync();
        }

        _promptEditorAttachments.Clear();
        foreach (var contribution in contributions)
        {
            var attachment = contribution.Attach(this);
            if (attachment is not null)
            {
                _promptEditorAttachments.Add(attachment);
            }
        }

        return this;
    }

    internal IReadOnlyList<ProjectFileReferencePopupItem> ProjectFileReferenceItems
        => _projectFileReferencePopupController?.Items ?? [];

    internal int ProjectFileReferenceSelectedIndex
        => _projectFileReferencePopupController?.SelectedIndex ?? -1;

    internal string ProjectFileReferenceQueryText
        => _projectFileReferencePopupController?.QueryText ?? string.Empty;

    internal void RefreshProjectFileReferencePopup()
        => _projectFileReferencePopupController?.HandleEditorStateChanged();

    Visual IProjectFileReferencePopupHost.Visual => this;

    Visual IPluginPromptEditorHost.Visual => this;

    string? IPluginPromptEditorHost.ProjectPath => _getPromptReferenceProjectRoot?.Invoke();

    void IProjectFileReferencePopupHost.FocusPromptEditor()
        => App?.Focus(this);

    void IPluginPromptEditorHost.FocusPromptEditor()
        => App?.Focus(this);
}
