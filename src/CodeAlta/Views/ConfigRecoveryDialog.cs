using System.Text;
using CodeAlta.Catalog;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class ConfigRecoveryDialog
{
    private readonly string _configPath;
    private readonly Action _saveAndContinue;
    private readonly Action _exit;
    private readonly CodeEditor _editor;
    private readonly Button _saveButton;
    private readonly Button _exitButton;
    private Dialog? _dialog;
    private CodeAltaConfigValidationResult _validation;

    public ConfigRecoveryDialog(
        string configPath,
        string initialText,
        CodeAltaConfigValidationResult initialValidation,
        Action saveAndContinue,
        Action exit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(saveAndContinue);
        ArgumentNullException.ThrowIfNull(exit);

        _configPath = configPath;
        _saveAndContinue = saveAndContinue;
        _exit = exit;
        _validation = initialValidation;

        _editor = ConfigTomlEditor.Create(initialText ?? string.Empty, _configPath);
        _editor.TextDocument.Changed += OnEditorChanged;
        var diagnosticMargin = CodeEditor.CreateDiffIndicatorMargin(
            lineIndex => !_validation.IsValid && _validation.Line is { } line && lineIndex == line - 1
                ? new Rune('●')
                : null);
        _editor.LeftMargins.Insert(0, diagnosticMargin);

        _saveButton = new Button($"{NerdFont.MdContentSaveCheckOutline} Save and Continue") { Tone = ControlTone.Success };
        _saveButton.IsEnabled(() => _validation.IsValid);
        _saveButton.Click(SaveAndContinue);

        _exitButton = new Button($"{NerdFont.MdExitRun} Exit") { Tone = ControlTone.Error };
        _exitButton.Click(Exit);
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
            new Markup($"[bold warning]{NerdFont.MdAlertCircleOutline} CodeAlta could not load your configuration[/]"),
            new TextBlock(
                    "Fix the TOML below to continue startup. CodeAlta only parses and validates this file here; no providers, sessions, or background agent work are started until the configuration can be loaded.")
                .Wrap(true),
            new Markup($"[dim]{AnsiMarkup.Escape(_configPath)}[/]") { Wrap = true })
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var editorFrame = new Border(
            new ScrollViewer(_editor.Stretch(), focusable: false)
                .IsTabStop(false)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var status = new Markup(BuildStatusMarkup)
        {
            Wrap = true,
            HorizontalAlignment = Align.Stretch,
        };

        var buttons = new HStack(_exitButton, _saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var dialog = new Dialog()
            .Title("Recover ~/.alta/config.toml")
            .BottomLeftText(new Markup("[dim]Ctrl+S Save and Continue · Ctrl+Q Exit · Ctrl+F Find · Ctrl+G Go to line[/]"))
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
            LabelMarkup = "Save and Continue",
            DescriptionMarkup = "Save the repaired config and continue CodeAlta startup.",
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.CommandBar,
            Importance = CommandImportance.Primary,
            CanExecute = _ => _validation.IsValid,
            Execute = _ => SaveAndContinue(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ConfigRecovery.Exit",
            LabelMarkup = "Exit",
            DescriptionMarkup = "Exit CodeAlta without changing the config.",
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
        _validation = CodeAltaConfigStore.ValidateGlobalConfigContent(GetEditorText(), _configPath);
    }

    private string BuildStatusMarkup()
    {
        if (_validation.IsValid)
        {
            return $"[success]{NerdFont.MdCheckCircleOutline} Configuration can be loaded. Save and Continue is available.[/]";
        }

        var location = _validation.Line is { } line
            ? $"Line {line}, column {_validation.Column.GetValueOrDefault(1)}: "
            : string.Empty;
        return $"[error]{NerdFont.MdAlertCircleOutline} {AnsiMarkup.Escape(location + (_validation.Message ?? "Configuration is invalid."))}[/]";
    }

    private void SaveAndContinue()
    {
        if (!_validation.IsValid)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(_configPath, GetEditorText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _validation = new CodeAltaConfigValidationResult(false, $"Unable to save config file: {ex.Message}", null, null);
            return;
        }

        _dialog?.Close();
        _dialog = null;
        _saveAndContinue();
    }

    private void Exit()
    {
        _dialog?.Close();
        _dialog = null;
        _exit();
    }

    private string GetEditorText()
    {
        var snapshot = _editor.TextDocument.CurrentSnapshot;
        if (snapshot.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(snapshot.Length, snapshot, static (span, currentSnapshot) => currentSnapshot.CopyTo(0, span));
    }
}
