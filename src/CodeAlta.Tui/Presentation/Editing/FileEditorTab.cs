using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Presentation.Styling;
using CodeAlta.Catalog;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Tui.Presentation.Editing;

internal sealed partial class FileEditorTab : IAsyncDisposable
{
    private readonly FileEditorSessionState _sessionState;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private readonly FileSystemWatcher? _watcher;
    private readonly Button _saveButton;
    private readonly Button _reloadButton;
    private readonly State<bool> _wordWrap = new(true);
    private readonly TextFileCodec _textFiles;
    private TextFileDocument _document;
    private int _pendingSaves;
    private TextFileSnapshot _snapshot;
    private TextFileRevision? _conflictingRevision;
    private bool _suppressEditorChanged;
    private ExternalRefreshAttachment? _externalRefreshAttachment;
    private bool _disposed;

    private FileEditorTab(
        ProjectFileSearchItem item,
        ProjectFileAppearance appearance,
        TextFileCodec textFiles,
        TextFileDocument document,
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
        _textFiles = textFiles;
        _document = document;
        IsReadOnly = document.IsReadOnly;
        _snapshot = snapshot;
        _sessionState = new FileEditorSessionState(snapshot.Text, snapshot.LastWriteTimeUtc);

        Editor = CreateEditor(item, snapshot.Text);
        Editor.TextDocument.Changed += OnEditorDocumentChanged;
        _saveButton = new Button(SR.T("Save")) { Tone = ControlTone.Success };
        _saveButton.Click(() => _ = SaveAsync());
        _saveButton.IsEnabled(() => IsDirty && !IsReadOnly);

        _reloadButton = new Button(SR.T("Reload")) { Tone = ControlTone.Warning };
        _reloadButton.Click(() => _ = ReloadAsync(confirmWhenDirty: true));
        _reloadButton.IsVisible(() => HasExternalChanges && ExistsOnDisk);
        _reloadButton.IsEnabled(() => HasExternalChanges && ExistsOnDisk);

        Root = new EditorRoot(this, BuildRoot())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
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
    public partial bool IsReadOnly { get; private set; }

    public bool TryProtect(TextFileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.FullPath, FullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The protection request must identify this editor document.", nameof(document));
        }
        if (IsReadOnly || !document.IsReadOnly) return true;
        if (_pendingSaves != 0) return false;
        _document = document;
        IsReadOnly = true;
        UpdateUiState(); // Do not replace text, undo history, saved snapshot or conflict revision.
        return true;
    }

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
        TextFileCodec textFiles,
        Action<string, bool, StatusTone> setStatus,
        CancellationToken cancellationToken = default,
        TextFileDocument? document = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(textFiles);
        ArgumentNullException.ThrowIfNull(setStatus);

        document ??= new TextFileDocument(item.FullPath);
        var snapshot = await textFiles.LoadAsync(document, cancellationToken);
        return new FileEditorTab(item, appearance, textFiles, document, snapshot, setStatus);
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

        if (IsReadOnly)
        {
            ShowActionDialog(SR.T("Unsaved changes"),
                [SR.T("Read-only skill document. Unsaved text is retained for copying; saving is disabled.")],
                [new DialogAction(SR.T("Cancel"), ControlTone.Default, static () => Task.CompletedTask),
                 new DialogAction(SR.T("Discard"), ControlTone.Error, closeTabAsync)]);
            return false;
        }

        ShowActionDialog(
            SR.T("Unsaved changes"),
            [
                SR.T("Save changes to '{0}' before closing?", Item.Basename),
                SR.T("Choose Save to keep your edits, or Discard to close the tab without saving.")
            ],
            [
                new DialogAction(SR.T("Cancel"), ControlTone.Default, static () => Task.CompletedTask),
                new DialogAction(SR.T("Discard"), ControlTone.Error, closeTabAsync),
                new DialogAction(
                    SR.T("Save"),
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _externalRefreshAttachment, null);
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
                    CreateIconBlock(),
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

    private TextBlock CreateIconBlock()
    {
        TextBlock? icon = null;
        icon = new TextBlock(Appearance.Icon)
        {
            Wrap = false,
            IsSelectable = false,
        }.Style(() => TextBlockStyle.Default with
        {
            Foreground = UiPalette.GetProjectFileIconColor(icon!.GetTheme(), Appearance.Category, Appearance.IconForeground),
        });
        return icon;
    }

    public void QueueExternalStateRefresh()
    {
        // Watcher callbacks can arrive before mounting, after detaching, or after
        // disposal. Never access bindable state or the global dispatcher here.
        var attachment = Volatile.Read(ref _externalRefreshAttachment);
        if (attachment is null || Interlocked.Exchange(ref attachment.RefreshQueued, 1) != 0)
        {
            return;
        }

        var posted = false;
        try
        {
            // TerminalApp.Post targets this app's queue even if its dispatcher
            // detaches concurrently. A stale callback is invalidated below.
            attachment.App.Post(
                () =>
                {
                    Interlocked.Exchange(ref attachment.RefreshQueued, 0);
                    if (ReferenceEquals(Volatile.Read(ref _externalRefreshAttachment), attachment))
                    {
                        RefreshExternalState();
                    }
                });
            posted = true;
        }
        finally
        {
            if (!posted)
            {
                Interlocked.Exchange(ref attachment.RefreshQueued, 0);
            }
        }
    }

    private void AttachExternalStateRefresh(TerminalApp app)
    {
        if (_disposed)
        {
            return;
        }

        // Each attachment owns its coalescing flag: an old queued callback cannot
        // clear a new attachment's flag or update a disposed/re-attached editor.
        Volatile.Write(ref _externalRefreshAttachment, new ExternalRefreshAttachment(app));
        QueueExternalStateRefresh();
    }

    private Task<bool> SaveAsync() => SaveAsync(_snapshot.Revision);

    private async Task<bool> SaveAsync(TextFileRevision expectedRevision)
    {
        try
        {
            var result = await SaveCurrentTextAsync(expectedRevision);
            if (result.IsConflict)
            {
                ShowActionDialog(
                    SR.T("File changed on disk"),
                    [
                        SR.T("'{0}' has changed on disk since it was opened or last saved.", Item.Basename),
                        SR.T("Overwrite keeps your editor changes. Reload discards them and loads the latest file from disk.")
                    ],
                    [
                        new DialogAction(SR.T("Cancel"), ControlTone.Default, static () => Task.CompletedTask),
                        new DialogAction(SR.T("Reload"), ControlTone.Warning, () => ReloadAsync(confirmWhenDirty: false)),
                        new DialogAction(SR.T("Overwrite"), ControlTone.Success, async () => { await SaveAsync(result.CurrentRevision); })
                    ]);
                return false;
            }

            return !IsDirty;
        }
        catch (Exception ex)
        {
            _setStatus(SR.T("Failed to save '{0}': {1}", Item.Basename, ex.Message), false, StatusTone.Error);
            return false;
        }
    }

    internal async Task<TextFileSaveResult> SaveCurrentTextAsync(TextFileRevision expectedRevision)
    {
        _pendingSaves++;
        TextFileSaveResult result;
        try
        {
            result = await _textFiles.SaveAsync(_document, GetEditorText(), _snapshot, expectedRevision);
        }
        finally
        {
            _pendingSaves--;
        }

        if (result.IsConflict)
        {
            _conflictingRevision = result.CurrentRevision;
            _sessionState.MarkConflict(result.CurrentRevision.Exists);
            UpdateUiState();
            _setStatus(SR.T("'{0}' has changed on disk since it was opened or last saved.", Item.Basename), false, StatusTone.Warning);
            return result;
        }

        _snapshot = result.Snapshot;
        _conflictingRevision = null;
        _sessionState.MarkSaved(_snapshot.Text, _snapshot.LastWriteTimeUtc);
        // Edits made while the save was awaiting I/O are not part of its acknowledgement.
        _sessionState.UpdateEditorText(GetEditorText());
        UpdateUiState();
        _setStatus(SR.T("Saved '{0}'.", Item.Basename), false, StatusTone.Ready);
        return result;
    }

    private async Task ReloadAsync(bool confirmWhenDirty)
    {
        if (confirmWhenDirty && IsDirty)
        {
            ShowActionDialog(
                SR.T("Reload from disk"),
                [
                    SR.T("Discard the unsaved edits in '{0}' and reload the latest copy from disk?", Item.Basename)
                ],
                [
                    new DialogAction(SR.T("Cancel"), ControlTone.Default, static () => Task.CompletedTask),
                    new DialogAction(SR.T("Reload"), ControlTone.Warning, () => ReloadAsync(confirmWhenDirty: false))
                ]);
            return;
        }

        try
        {
            var snapshot = await _textFiles.LoadAsync(_document);
            _snapshot = snapshot;
            _conflictingRevision = null;
            ReplaceEditorDocument(snapshot.Text);
            _sessionState.MarkReloaded(snapshot.Text, snapshot.LastWriteTimeUtc);
            UpdateUiState();
            _setStatus(SR.T("Reloaded '{0}'.", Item.Basename), false, StatusTone.Ready);
        }
        catch (Exception ex)
        {
            _setStatus(SR.T("Failed to reload '{0}': {1}", Item.Basename, ex.Message), false, StatusTone.Error);
        }
    }

    private void RefreshExternalState()
    {
        try
        {
            var exists = File.Exists(FullPath);
            var lastWriteTimeUtc = exists ? File.GetLastWriteTimeUtc(FullPath) : (DateTimeOffset?)null;
            _sessionState.RefreshExternalState(exists, lastWriteTimeUtc);
            if (_conflictingRevision is not null)
            {
                _sessionState.MarkConflict(exists);
            }
            UpdateUiState();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
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
            Editor.TextDocument.Changed -= OnEditorDocumentChanged;
            Editor.TextDocument = new FileEditorTextDocument(text, () => IsReadOnly);
            Editor.TextDocument.Changed += OnEditorDocumentChanged;
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
        var locationText = new TextBlock(() => SR.T("{0} · Ln {1}, Col {2}", StatusText, Editor.Line, Editor.Column))
        {
            Wrap = false,
            IsSelectable = false,
        };
        var pathText = new TextBlock(BuildPathText())
        {
            Wrap = false,
            IsSelectable = false,
        };
        var shortcutText = new Markup(() => $"[dim]{(IsReadOnly ? SR.T("Read-only · Ctrl+C Copy · Ctrl+F Find · Ctrl+G Go to line") : SR.T("Ctrl+S Save · Ctrl+F Find · Ctrl+H Replace · Ctrl+G Go to line"))}[/]")
        {
            Wrap = false,
        };
        var wrapCheckBox = new CheckBox(SR.T("Wrap")).IsChecked(_wordWrap);

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

        var scrollableEditor = CodeEditorFactory.CreateScrollViewer(Editor);

        return new DockLayout()
            .Content(scrollableEditor)
            .Bottom(footer)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
    }

    private CodeEditor CreateEditor(ProjectFileSearchItem item, string text)
    {
        var editor = CodeEditorFactory.Create(
            null,
            new CodeEditorFactoryOptions
            {
                FileName = item.FullPath,
                WordWrapState = _wordWrap,
            });
        editor.TextDocument = new FileEditorTextDocument(text, () => IsReadOnly);
        foreach (var command in editor.Commands.Where(static command => command.Id is
            "TextEditor.Undo" or "TextEditor.Redo" or "TextEditor.Cut" or "TextEditor.Paste" or "TextEditor.Replace").ToArray())
        {
            editor.RemoveCommand(command.Id);
            editor.AddCommand(new Command
            {
                Id = command.Id, LabelMarkup = command.LabelMarkup, DescriptionMarkup = command.DescriptionMarkup,
                Gesture = command.Gesture, Importance = command.Importance, Presentation = command.Presentation,
                CanExecute = visual => !IsReadOnly && command.CanExecuteFor(visual),
                Execute = visual => { if (!IsReadOnly) command.Execute(visual); },
            });
        }
        editor.AddCommand(
            new Command
            {
                Id = "CodeAlta.File.Save",
                LabelMarkup = SR.T("Save"),
                DescriptionMarkup = SR.T("Save the current file."),
                Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandBar,
                Importance = CommandImportance.Primary,
                Execute = _ =>
                {
                    var ignored = SaveAsync();
                },
                CanExecute = _ => IsDirty && !IsReadOnly,
            });
        editor.AddCommand(
            new Command
            {
                Id = "CodeAlta.File.Reload",
                LabelMarkup = SR.T("Reload"),
                DescriptionMarkup = SR.T("Reload the current file from disk."),
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
        };
        watcher.Changed += OnWatchedFileChanged;
        watcher.Created += OnWatchedFileChanged;
        watcher.Deleted += OnWatchedFileChanged;
        watcher.Renamed += OnWatchedFileRenamed;
        watcher.EnableRaisingEvents = true;
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
        if (IsReadOnly)
        {
            var status = SR.T("Read-only") + (IsDirty ? "*" : string.Empty);
            return !ExistsOnDisk ? status + " · " + SR.T("Deleted on disk") :
                HasExternalChanges ? status + " · " + SR.T("Changed on disk · reload available") : status;
        }

        if (!ExistsOnDisk)
        {
            return IsDirty
                ? SR.T("Deleted on disk · save recreates")
                : SR.T("Deleted on disk");
        }

        if (HasExternalChanges)
        {
            return IsDirty
                ? SR.T("Changed on disk · save asks before overwrite")
                : SR.T("Changed on disk · reload available");
        }

        return IsDirty ? SR.T("Modified") : SR.T("Saved");
    }

    private string BuildPathText()
        => string.IsNullOrWhiteSpace(Item.RelativePath) ? FullPath : Item.RelativePath;

    private string GetEditorText()
        => CodeEditorFactory.GetText(Editor);

    private void ShowActionDialog(string title, IReadOnlyList<string> bodyLines, IReadOnlyList<DialogAction> actions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bodyLines);
        ArgumentNullException.ThrowIfNull(actions);

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
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
            .BottomRightText(new Markup($"[dim]{SR.T("Esc Close")}[/]"))
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
                LabelMarkup = SR.T("Close"),
                DescriptionMarkup = SR.T("Close the dialog."),
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

    private sealed class ExternalRefreshAttachment(TerminalApp app)
    {
        public TerminalApp App { get; } = app;

        public int RefreshQueued;
    }

    private sealed class EditorRoot(FileEditorTab owner, Visual content) : Padder(content)
    {
        protected override void OnAttachedToApp(TerminalApp app)
        {
            base.OnAttachedToApp(app);
            owner.AttachExternalStateRefresh(app);
        }

        protected override void OnDetachedFromApp(TerminalApp app)
        {
            Volatile.Write(ref owner._externalRefreshAttachment, null);
            base.OnDetachedFromApp(app);
        }
    }

    private sealed record DialogAction(string Label, ControlTone Tone, Func<Task> Execute);
}
