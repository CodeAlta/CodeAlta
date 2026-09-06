using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Tui.Presentation.Editing;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Tui.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Tui.Views;

internal sealed class ConfigRecoveryDialog
{
    private readonly ConfigRecoveryService _recovery;
    private readonly Action<Action> _confirmReload;
    private string _baselineText;
    private readonly Action _saveAndContinue;
    private readonly Action _exit;
    private readonly CodeEditor _editor;
    private readonly Button _saveButton;
    private readonly Button _exitButton;
    private readonly Button _reloadButton;
    private readonly State<int> _editVersion = new(0);
    private Dialog? _dialog;
    private CodeAltaConfigValidationResult _validation;

    public ConfigRecoveryDialog(
        ConfigRecoveryService recovery,
        Action saveAndContinue,
        Action exit)
        : this(recovery, saveAndContinue, exit, null)
    {
    }

    internal ConfigRecoveryDialog(ConfigRecoveryService recovery, Action saveAndContinue, Action exit, Action<Action>? confirmReload)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(saveAndContinue);
        ArgumentNullException.ThrowIfNull(exit);

        _recovery = recovery;
        _confirmReload = confirmReload ?? ConfirmReload;
        _saveAndContinue = saveAndContinue;
        _exit = exit;
        _baselineText = recovery.Snapshot?.Text ?? string.Empty;
        _validation = recovery.Validate(_baselineText);

        _editor = ConfigTomlEditor.Create(_baselineText, recovery.ConfigPath);
        _editor.TextDocument.Changed += OnEditorChanged;
        var diagnosticMargin = CodeEditor.CreateDiffIndicatorMargin(
            lineIndex => !_validation.IsValid && _validation.Line is { } line && lineIndex == line - 1
                ? new Rune('●')
                : null);
        _editor.LeftMargins.Insert(0, diagnosticMargin);

        _saveButton = new Button($"{TerminalIcons.MdContentSaveCheckOutline} {SR.T("Save and Continue")}") { Tone = ControlTone.Success };
        _saveButton.IsEnabled(CanSave);
        _saveButton.Click(SaveAndContinue);

        _exitButton = new Button($"{TerminalIcons.MdExitRun} {SR.T("Exit")}") { Tone = ControlTone.Error };
        _exitButton.Click(Exit);
        _reloadButton = new Button(SR.T("Reload"));
        _reloadButton.Click(RequestReload);
    }

    public void Show(TerminalApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (_dialog is not null)
        {
            app.Focus(_editor);
            return;
        }

        _dialog = BuildDialog();
        var terminalSize = Terminal.Instance.IsInitialized ? Terminal.Instance.Size : default;
        ResponsiveDialogSize.Apply(
            _dialog,
            ResolveDialogBounds(app.Root.GetAbsoluteBounds(), terminalSize),
            minWidth: 80,
            minHeight: 24,
            widthFactor: 0.8,
            heightFactor: 0.8);
        _dialog.Show();
        if (_validation.Line is { } line)
        {
            _editor.GoToLine(line, Math.Max(1, _validation.Column ?? 1));
        }

        app.Focus(_editor);
    }

    internal static Rectangle? ResolveDialogBounds(Rectangle? rootBounds, TerminalSize terminalSize)
    {
        var width = Math.Max(rootBounds?.Width ?? 0, terminalSize.Columns);
        var height = Math.Max(rootBounds?.Height ?? 0, terminalSize.Rows);
        if (width <= 0 || height <= 0)
        {
            return rootBounds;
        }

        return new Rectangle(rootBounds?.X ?? 0, rootBounds?.Y ?? 0, width, height);
    }

    private Dialog BuildDialog()
    {
        var heading = new VStack(
            new Markup($"[bold warning]{TerminalIcons.MdAlertCircleOutline} {SR.T("CodeAlta could not load your configuration")}[/]"),
            new TextBlock(
                    SR.T("Repair the TOML and save to continue. Recovery only reads and validates configuration; it does not initialize providers, plugins or sessions."))
                .Wrap(true),
            new Markup($"[dim]{AnsiMarkup.Escape(_recovery.ConfigPath)}[/]") { Wrap = true })
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var editorFrame = new Border(CodeEditorFactory.CreateScrollViewer(_editor))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var status = new Markup(BuildStatusMarkup)
        {
            Wrap = true,
            HorizontalAlignment = Align.Stretch,
        };

        var buttons = new HStack(_exitButton, _reloadButton, _saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var dialog = new Dialog()
            .Title(SR.T("Recover ~/.alta/config.toml"))
            .BottomLeftText(new Markup($"[dim]{SR.T("Ctrl+S Save and Continue · Ctrl+Q Exit · Ctrl+F Find · Ctrl+G Go to line")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(new DockLayout()
                .Top(heading)
                .Content(editorFrame)
                .Bottom(new VStack(status, buttons)
                {
                    Spacing = 1,
                    HorizontalAlignment = Align.Stretch,
                })
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch));
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ConfigRecovery.SaveAndContinue",
            LabelMarkup = SR.T("Save and Continue"),
            DescriptionMarkup = SR.T("Save the repaired config and continue CodeAlta startup."),
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.CommandBar,
            Importance = CommandImportance.Primary,
            CanExecute = _ => CanSave(),
            Execute = _ => SaveAndContinue(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ConfigRecovery.Exit",
            LabelMarkup = SR.T("Exit"),
            DescriptionMarkup = SR.T("Exit CodeAlta without changing the config."),
            Gesture = new KeyGesture(TerminalChar.CtrlQ, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.CommandBar,
            Importance = CommandImportance.Primary,
            Execute = _ => Exit(),
        });
        return dialog;
    }

    private void OnEditorChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        _validation = _recovery.Validate(GetEditorText());
        _editVersion.Value++;
    }

    internal string BuildStatusMarkup()
    {
        _ = _editVersion.Value;

        var failure = _recovery.Failure is { } message
            ? $"[error]{AnsiMarkup.Escape(message)}[/]\n"
            : string.Empty;
        if (_recovery.Snapshot is null)
        {
            failure += $"[warning]{SR.T("Reload the config successfully before saving. No readable baseline is available.")}[/]\n";
        }
        if (_validation.IsValid)
        {
            return failure + $"[success]{TerminalIcons.MdCheckCircleOutline} {SR.T("TOML is valid. Startup continues only after a successful save.")}[/]";
        }

        var location = _validation.Line is { } line
            ? SR.T("Line {0}, column {1}: ", line, _validation.Column.GetValueOrDefault(1))
            : string.Empty;
        return failure + $"[error]{TerminalIcons.MdAlertCircleOutline} {AnsiMarkup.Escape(location + (_validation.Message ?? SR.T("Configuration is invalid.")))}[/]";
    }

    internal bool CanSave()
    {
        _ = _editVersion.Value;
        return _validation.IsValid && _recovery.Snapshot is not null;
    }

    internal void SaveAndContinue()
    {
        if (!CanSave())
        {
            return;
        }

        if (!_recovery.Save(GetEditorText()))
        {
            _editVersion.Value++;
            return;
        }

        _dialog?.Close();
        _dialog = null;
        _saveAndContinue();
    }

    internal void RequestReload()
    {
        if (!string.Equals(GetEditorText(), _baselineText, StringComparison.Ordinal))
        {
            _confirmReload(Reload);
            return;
        }
        Reload();
    }

    private void ConfirmReload(Action reload)
        => new ConfirmationDialog(SR.T("Discard Config Changes?"),
            [SR.T("Reload discards your unsaved config edits. Copy any text you want to reapply first.")],
            SR.T("Reload"), ControlTone.Error,
            () => { reload(); return Task.CompletedTask; },
            () => _dialog?.GetAbsoluteBounds(), () => _editor).Show();

    private void Reload()
    {
        if (_recovery.Reload())
        {
            _baselineText = _recovery.Snapshot!.Text;
            _editor.TextDocument.Replace(0, _editor.TextDocument.CurrentSnapshot.Length, _baselineText.AsSpan());
            _validation = _recovery.Validate(_baselineText);
        }
        _editVersion.Value++;
    }

    internal void Exit()
    {
        _dialog?.Close();
        _dialog = null;
        _exit();
    }

    private string GetEditorText()
        => CodeEditorFactory.GetText(_editor);
}
