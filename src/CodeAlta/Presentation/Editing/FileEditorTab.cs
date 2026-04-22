using System.Text;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Styling;
using CodeAlta.Search;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Presentation.Editing;

internal sealed partial class FileEditorTab : IAsyncDisposable
{
    private readonly FileEditorSessionState _sessionState;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly FileSystemWatcher? _watcher;
    private readonly Button _saveButton;
    private readonly Button _reloadButton;
    private readonly State<bool> _wordWrap = new(true);
    private Encoding _encoding;
    private bool _hasByteOrderMark;
    private bool _suppressEditorChanged;
    private int _refreshExternalStateQueued;

    private FileEditorTab(
        ProjectFileSearchItem item,
        ProjectFileAppearance appearance,
        TextFileSnapshot snapshot,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(setStatus);

        Item = item;
        Appearance = appearance;
        TabId = CreateTabId(item.FullPath);
        _setStatus = setStatus;
        _encoding = snapshot.Encoding;
        _hasByteOrderMark = snapshot.HasByteOrderMark;
        _sessionState = new FileEditorSessionState(snapshot.Text, snapshot.LastWriteTimeUtc);

        Editor = CreateEditor(item, snapshot.Text);
        Editor.TextDocument.Changed += OnEditorDocumentChanged;
        _saveButton = new Button("Save") { Tone = ControlTone.Success };
        _saveButton.Click(() => _ = SaveAsync());
        _saveButton.IsEnabled(() => IsDirty);

        _reloadButton = new Button("Reload") { Tone = ControlTone.Warning };
        _reloadButton.Click(() => _ = ReloadAsync(confirmWhenDirty: true));
        _reloadButton.IsVisible(() => HasExternalChanges && ExistsOnDisk);
        _reloadButton.IsEnabled(() => HasExternalChanges && ExistsOnDisk);

        Root = BuildRoot();
        _watcher = CreateWatcher(item.FullPath);
        UpdateUiState();
    }

    public ProjectFileSearchItem Item { get; }

    public ProjectFileAppearance Appearance { get; }

    public string TabId { get; }

    public string FullPath => Item.FullPath;

    public CodeEditor Editor { get; }

    public Visual Root { get; }

    [Bindable]
    public partial bool IsDirty { get; private set; }

    [Bindable]
    public partial bool HasExternalChanges { get; private set; }

    [Bindable]
    public partial bool ExistsOnDisk { get; private set; }

    [Bindable]
    public partial string StatusText { get; private set; }

    public static async Task<FileEditorTab> CreateAsync(
        ProjectFileSearchItem item,
        ProjectFileAppearance appearance,
        Action<string, bool, StatusTone> setStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(setStatus);

        var snapshot = await TextFileCodec.LoadAsync(item.FullPath, cancellationToken);
        return new FileEditorTab(item, appearance, snapshot, setStatus);
    }

    public void Focus()
    {
        if (Editor.App is { } editorApp)
        {
            Editor.GoToLine(Editor.Line, Editor.Column);
            editorApp.Focus(Editor);
        }
        else if (Root.App is { } rootApp)
        {
            Editor.GoToLine(Editor.Line, Editor.Column);
            rootApp.Focus(Editor);
        }

        Root.Dispatcher.Post(
            () =>
            {
                Editor.GoToLine(Editor.Line, Editor.Column);
                (Editor.App ?? Root.App)?.Focus(Editor);
            });
    }

    public async Task<bool> RequestCloseAsync(Func<Task> closeTabAsync)
    {
        ArgumentNullException.ThrowIfNull(closeTabAsync);

        if (!IsDirty)
        {
            await closeTabAsync();
            return true;
        }

        ShowActionDialog(
            "Unsaved changes",
            [
                $"Save changes to '{Item.Basename}' before closing?",
                "Choose Save to keep your edits, or Discard to close the tab without saving."
            ],
            [
                new DialogAction("Cancel", ControlTone.Default, static () => Task.CompletedTask),
                new DialogAction("Discard", ControlTone.Error, closeTabAsync),
                new DialogAction(
                    "Save",
                    ControlTone.Success,
                    async () =>
                    {
                        if (await SaveAsync())
                        {
                            await closeTabAsync();
                        }
                    })
            ]);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Editor.TextDocument.Changed -= OnEditorDocumentChanged;

        if (_watcher is not null)
        {
            _watcher.Changed -= OnWatchedFileChanged;
            _watcher.Created -= OnWatchedFileChanged;
            _watcher.Deleted -= OnWatchedFileChanged;
            _watcher.Renamed -= OnWatchedFileRenamed;
            _watcher.Dispose();
        }

        await Task.CompletedTask;
    }

    public Visual CreateTabHeader(Func<Func<Visual>, ComputedVisual> createComputedVisual)
    {
        ArgumentNullException.ThrowIfNull(createComputedVisual);

        return createComputedVisual(
            () =>
            {
                var title = Item.Basename + (IsDirty ? "*" : string.Empty);
                return new HStack(
                [
                    new TextBlock(Appearance.Icon)
                    {
                        Wrap = false,
                        IsSelectable = false,
                    }.Style(TextBlockStyle.Default with { Foreground = Appearance.IconForeground }),
                    new TextBlock(CompactTitle(title))
                    {
                        Wrap = false,
                        IsSelectable = false,
                    },
                ])
                {
                    Spacing = 1,
                };
            });
    }

    public void QueueExternalStateRefresh()
    {
        if (Interlocked.Exchange(ref _refreshExternalStateQueued, 1) != 0)
        {
            return;
        }

        Root.Dispatcher.Post(
            () =>
            {
                Interlocked.Exchange(ref _refreshExternalStateQueued, 0);
                _ = RefreshExternalStateAsync();
            });
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            await RefreshExternalStateAsync();
            if (HasExternalChanges)
            {
                ShowActionDialog(
                    "File changed on disk",
                    [
                        $"'{Item.Basename}' has changed on disk since it was opened or last saved.",
                        "Overwrite keeps your editor changes. Reload discards them and loads the latest file from disk."
                    ],
                    [
                        new DialogAction("Cancel", ControlTone.Default, static () => Task.CompletedTask),
                        new DialogAction("Reload", ControlTone.Warning, () => ReloadAsync(confirmWhenDirty: false)),
                        new DialogAction("Overwrite", ControlTone.Success, SaveCurrentTextAsync)
                    ]);
                return false;
            }

            await SaveCurrentTextAsync();
            return true;
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to save '{Item.Basename}': {ex.Message}", false, StatusTone.Error);
            return false;
        }
    }

    private async Task SaveCurrentTextAsync()
    {
        var snapshot = await TextFileCodec.SaveAsync(
            FullPath,
            GetEditorText(),
            _encoding,
            _hasByteOrderMark);

        _encoding = snapshot.Encoding;
        _hasByteOrderMark = snapshot.HasByteOrderMark;
        _sessionState.MarkSaved(snapshot.Text, snapshot.LastWriteTimeUtc);
        UpdateUiState();
        _setStatus($"Saved '{Item.Basename}'.", false, StatusTone.Ready);
    }

    private async Task ReloadAsync(bool confirmWhenDirty)
    {
        if (confirmWhenDirty && IsDirty)
        {
            ShowActionDialog(
                "Reload from disk",
                [
                    $"Discard the unsaved edits in '{Item.Basename}' and reload the latest copy from disk?"
                ],
                [
                    new DialogAction("Cancel", ControlTone.Default, static () => Task.CompletedTask),
                    new DialogAction("Reload", ControlTone.Warning, () => ReloadAsync(confirmWhenDirty: false))
                ]);
            return;
        }

        try
        {
            var snapshot = await TextFileCodec.LoadAsync(FullPath);
            _encoding = snapshot.Encoding;
            _hasByteOrderMark = snapshot.HasByteOrderMark;
            ReplaceEditorDocument(snapshot.Text);
            _sessionState.MarkReloaded(snapshot.Text, snapshot.LastWriteTimeUtc);
            UpdateUiState();
            _setStatus($"Reloaded '{Item.Basename}'.", false, StatusTone.Ready);
        }
        catch (Exception ex)
        {
            _setStatus($"Failed to reload '{Item.Basename}': {ex.Message}", false, StatusTone.Error);
        }
    }

    private async Task RefreshExternalStateAsync()
    {
        try
        {
            var exists = File.Exists(FullPath);
            var lastWriteTimeUtc = exists ? File.GetLastWriteTimeUtc(FullPath) : (DateTimeOffset?)null;
            _sessionState.RefreshExternalState(exists, lastWriteTimeUtc);
            UpdateUiState();
            await Task.CompletedTask;
        }
        catch
        {
        }
    }

    private void OnEditorDocumentChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_suppressEditorChanged)
        {
            return;
        }

        _sessionState.UpdateEditorText(GetEditorText());
        UpdateUiState();
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
    {
        _ = sender;
        _ = e;
        QueueExternalStateRefresh();
    }

    private void OnWatchedFileRenamed(object sender, RenamedEventArgs e)
    {
        _ = sender;
        _ = e;
        QueueExternalStateRefresh();
    }

    private void ReplaceEditorDocument(string text)
    {
        var currentLine = Editor.Line;
        var currentColumn = Editor.Column;

        _suppressEditorChanged = true;
        try
        {
            Editor.TextDocument = new TextDocument(text);
            Editor.GoToLine(currentLine, currentColumn);
            Editor.ClearUndoHistory();
        }
        finally
        {
            _suppressEditorChanged = false;
        }
    }

    private Visual BuildRoot()
    {
        var locationText = new TextBlock(() => $"{StatusText} · Ln {Editor.Line}, Col {Editor.Column}")
        {
            Wrap = false,
            IsSelectable = false,
        };
        var pathText = new TextBlock(BuildPathText())
        {
            Wrap = false,
            IsSelectable = false,
        };
        var shortcutText = new Markup("[dim]Ctrl+S Save · Ctrl+F Find · Ctrl+H Replace · Ctrl+G Go to line[/]")
        {
            Wrap = false,
        };
        var wrapCheckBox = new CheckBox("Wrap").IsChecked(_wordWrap);

        var actions = new HStack(
        [
            _reloadButton,
            _saveButton,
            wrapCheckBox,
            shortcutText,
        ])
        {
            Spacing = 1,
        };

        var footer = new Footer()
            .Left(locationText)
            .Center(pathText)
            .Right(actions);

        var scrollableEditor = new ScrollViewer(Editor.Stretch(), focusable: false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        return new DockLayout()
            .Content(scrollableEditor)
            .Bottom(footer)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
    }

    private CodeEditor CreateEditor(ProjectFileSearchItem item, string text)
    {
        var editor = new CodeEditor()
            .AutoFocus(true)
            .WordWrap(_wordWrap)
            .ShowLineNumbers(true)
            .HighlightCurrentLine(true)
            .MinHeight(12);
        editor.TextDocument = new TextDocument(text);
        editor.SyntaxHighlighter = CreateSyntaxHighlighter(item.FullPath);
        editor.AddCommand(
            new Command
            {
                Id = "CodeAlta.File.Save",
                LabelMarkup = "Save",
                DescriptionMarkup = "Save the current file.",
                Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandBar,
                Importance = CommandImportance.Primary,
                Execute = _ =>
                {
                    var ignored = SaveAsync();
                },
                CanExecute = _ => IsDirty,
            });
        editor.AddCommand(
            new Command
            {
                Id = "CodeAlta.File.Reload",
                LabelMarkup = "Reload",
                DescriptionMarkup = "Reload the current file from disk.",
                Presentation = CommandPresentation.CommandBar,
                Importance = CommandImportance.Secondary,
                Execute = _ =>
                {
                    var ignored = ReloadAsync(confirmWhenDirty: true);
                },
                CanExecute = _ => HasExternalChanges && ExistsOnDisk,
            });
        return editor;
    }

    private FileSystemWatcher? CreateWatcher(string fullPath)
    {
        var directoryPath = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directoryPath))
        {
            return null;
        }

        var watcher = new FileSystemWatcher(directoryPath, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnWatchedFileChanged;
        watcher.Created += OnWatchedFileChanged;
        watcher.Deleted += OnWatchedFileChanged;
        watcher.Renamed += OnWatchedFileRenamed;
        return watcher;
    }

    private void UpdateUiState()
    {
        IsDirty = _sessionState.IsDirty;
        HasExternalChanges = _sessionState.HasExternalChanges;
        ExistsOnDisk = _sessionState.ExistsOnDisk;
        StatusText = BuildStatusText();
    }

    private string BuildStatusText()
    {
        if (!ExistsOnDisk)
        {
            return IsDirty
                ? "Deleted on disk · save recreates"
                : "Deleted on disk";
        }

        if (HasExternalChanges)
        {
            return IsDirty
                ? "Changed on disk · save asks before overwrite"
                : "Changed on disk · reload available";
        }

        return IsDirty ? "Modified" : "Saved";
    }

    private string BuildPathText()
        => string.IsNullOrWhiteSpace(Item.RelativePath) ? FullPath : Item.RelativePath;

    private string GetEditorText()
    {
        var snapshot = Editor.TextDocument.CurrentSnapshot;
        if (snapshot.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(snapshot.Length, snapshot, static (span, currentSnapshot) => currentSnapshot.CopyTo(0, span));
    }

    private void ShowActionDialog(string title, IReadOnlyList<string> bodyLines, IReadOnlyList<DialogAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bodyLines);
        ArgumentNullException.ThrowIfNull(actions);

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };

        Dialog? dialog = null;
        closeButton.Click(() => CloseDialog(dialog));

        var body = new VStack(
            bodyLines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => (Visual)new TextBlock(line).Wrap(true))
                .ToArray())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        var buttons = new HStack(
            actions
                .Select(action =>
                {
                    var button = new Button(action.Label) { Tone = action.Tone };
                    button.Click(() => _ = ExecuteDialogActionAsync(dialog, action.Execute));
                    return (Visual)button;
                })
                .ToArray())
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        dialog = new Dialog()
            .Title(title)
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(new VStack(body, buttons)
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                Spacing = 1,
            });
        ResponsiveDialogSize.Apply(dialog, DialogBoundsResolver.ResolveAppBounds(Editor), minWidth: 56, minHeight: 10, widthFactor: 0.62, heightFactor: 0.4);
        dialog.AddCommand(
            new Command
            {
                Id = "CodeAlta.FileEditor.Dialog.Close",
                LabelMarkup = "Close",
                DescriptionMarkup = "Close the dialog.",
                Gesture = new KeyGesture(TerminalKey.Escape),
                Importance = CommandImportance.Primary,
                Execute = _ => CloseDialog(dialog),
            });
        dialog.Show();
    }

    private static string CreateTabId(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return $"file:{fullPath}";
    }

    private static string CompactTitle(string title)
    {
        const int maxLength = 20;
        if (title.Length <= maxLength)
        {
            return title;
        }

        return title[..Math.Max(1, maxLength - 1)] + "…";
    }

    private static CodeEditorSyntaxHighlighter? CreateSyntaxHighlighter(string fullPath)
    {
        try
        {
            return new TextMateCodeEditorSyntaxHighlighter(
                new TextMateCodeEditorOptions
                {
                    FileName = fullPath,
                });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private Task ExecuteDialogActionAsync(Dialog? dialog, Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        CloseDialog(dialog);
        return execute();
    }

    private void CloseDialog(Dialog? dialog)
    {
        var app = dialog?.App ?? Root.App;
        dialog?.Close();
        app?.Focus(Editor);
    }

    private sealed record DialogAction(string Label, ControlTone Tone, Func<Task> Execute);
}
