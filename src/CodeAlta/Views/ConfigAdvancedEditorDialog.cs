using System.Text;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Editing;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class ConfigAdvancedEditorDialog
{
    private readonly IModelProviderDialogService _modelProviders;
    private readonly string _configPath;
    private readonly Action<ProviderConfigurationSaveResult> _onSaved;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly CodeEditor _editor;
    private readonly Button _saveButton;
    private readonly Button _closeButton;
    private readonly State<bool> _isSaving = new(false);
    private readonly State<int> _editVersion = new(0);
    private readonly State<string?> _saveError = new(null);
    private Dialog? _dialog;
    private string _loadedText;
    private CodeAltaConfigValidationResult _validation;

    public ConfigAdvancedEditorDialog(
        IModelProviderDialogService modelProviders,
        string configPath,
        string initialText,
        Action<ProviderConfigurationSaveResult> onSaved,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(modelProviders);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(onSaved);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _modelProviders = modelProviders;
        _configPath = configPath;
        _loadedText = initialText ?? string.Empty;
        _onSaved = onSaved;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
        _validation = _modelProviders.ValidateConfigurationContent(_loadedText);

        _editor = ConfigTomlEditor.Create(_loadedText, _configPath);
        _editor.TextDocument.Changed += OnEditorChanged;
        var diagnosticMargin = CodeEditor.CreateDiffIndicatorMargin(
            lineIndex => !_validation.IsValid && _validation.Line is { } line && lineIndex == line - 1
                ? new Rune('●')
                : null);
        _editor.LeftMargins.Insert(0, diagnosticMargin);

        _saveButton = new Button($"{TerminalIcons.MdContentSaveCheckOutline} Save and Apply") { Tone = ControlTone.Success };
        _saveButton.IsEnabled(() => !_isSaving.Value && _validation.IsValid && HasUnsavedChanges());
        _saveButton.Click(SaveAndApply);

        _closeButton = new Button($"{TerminalIcons.MdClose} Close") { Tone = ControlTone.Default };
        _closeButton.Click(RequestClose);
    }

    public void Show()
    {
        if (_dialog is not null)
        {
            _dialog.App?.Focus(_editor);
            return;
        }

        _dialog = BuildDialog();
        ResponsiveDialogSize.Apply(_dialog, _getBounds(), minWidth: 90, minHeight: 24, widthFactor: 0.80, heightFactor: 0.80);
        _dialog.Show();
        if (_validation.Line is { } line)
        {
            _editor.GoToLine(line, Math.Max(1, _validation.Column ?? 1));
        }

        _dialog.App?.Focus(_editor);
    }

    private Dialog BuildDialog()
    {
        var heading = new VStack(
            new Markup($"[bold primary]{TerminalIcons.MdCodeBraces} Advanced model provider TOML[/]"),
            new TextBlock(
                    "Edit the global CodeAlta configuration directly. Save and Apply is only available after the TOML parses and the provider configuration validates, so invalid edits are not written to disk.")
                .Wrap(true),
            new Markup($"[dim]{AnsiMarkup.Escape(_configPath)}[/]") { Wrap = true })
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

        var buttons = new HStack(_closeButton, _saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var dialog = new Dialog()
            .Title("Advanced Model Providers TOML")
            .BottomLeftText(new Markup("[dim]Ctrl+S Save and Apply · Esc Close · Ctrl+F Find · Ctrl+G Go to line[/]"))
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
            Id = "CodeAlta.Providers.Advanced.SaveAndApply",
            LabelMarkup = "Save and Apply",
            DescriptionMarkup = "Save the TOML configuration and refresh model providers.",
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.CommandBar,
            Importance = CommandImportance.Primary,
            CanExecute = _ => !_isSaving.Value && _validation.IsValid && HasUnsavedChanges(),
            Execute = _ => SaveAndApply(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Advanced.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the advanced TOML editor.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => RequestClose(),
        });
        return dialog;
    }

    private void OnEditorChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        _validation = _modelProviders.ValidateConfigurationContent(GetEditorText());
        _saveError.Value = null;
        _editVersion.Value++;
    }

    private string BuildStatusMarkup()
    {
        _ = _editVersion.Value;

        if (_isSaving.Value)
        {
            return "[primary]Saving configuration and refreshing model providers...[/]";
        }

        if (_saveError.Value is { } saveError)
        {
            return $"[error]{TerminalIcons.MdAlertCircleOutline} {AnsiMarkup.Escape(saveError)}[/]";
        }

        if (!_validation.IsValid)
        {
            var location = _validation.Line is { } line
                ? $"Line {line}, column {_validation.Column.GetValueOrDefault(1)}: "
                : string.Empty;
            return $"[error]{TerminalIcons.MdAlertCircleOutline} {AnsiMarkup.Escape(location + (_validation.Message ?? "Configuration is invalid."))}[/]";
        }

        if (!HasUnsavedChanges())
        {
            return "[success]Configuration is valid and matches the loaded file.[/]";
        }

        return $"[success]{TerminalIcons.MdCheckCircleOutline} Configuration can be loaded. Save and Apply is available.[/]";
    }

    private void SaveAndApply()
    {
        if (_isSaving.Value || !_validation.IsValid || !HasUnsavedChanges())
        {
            return;
        }

        var text = GetEditorText();
        _isSaving.Value = true;
        _saveError.Value = null;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var saveResult = await _modelProviders.SaveConfigurationContentAsync(text);
                    await PublishSaveResultAsync(text, saveResult);
                }
                catch (Exception ex)
                {
                    await PublishSaveFailureAsync(ex);
                }
            });
    }

    private Task PublishSaveResultAsync(string text, ProviderConfigurationSaveResult saveResult)
    {
        var dialog = _dialog;
        return dialog is null
            ? Task.CompletedTask
            : dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    _loadedText = text;
                    _validation = CodeAltaConfigValidationResult.Valid;
                    _editVersion.Value++;
                    _isSaving.Value = false;
                    _onSaved(saveResult);
                    Close();
                });
    }

    private Task PublishSaveFailureAsync(Exception exception)
    {
        var dialog = _dialog;
        return dialog is null
            ? Task.CompletedTask
            : dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    _saveError.Value = exception.GetBaseException().Message;
                    _isSaving.Value = false;
                });
    }

    private void RequestClose()
    {
        if (_isSaving.Value)
        {
            _saveError.Value = "Please wait for the current save operation to complete before closing this editor.";
            return;
        }

        if (!HasUnsavedChanges())
        {
            Close();
            return;
        }

        new ConfirmationDialog(
            "Discard TOML Changes?",
            ["You have unsaved TOML changes.", "Close the advanced editor without saving?"],
            "Discard",
            ControlTone.Error,
            () =>
            {
                Close();
                return Task.CompletedTask;
            },
            _getBounds,
            () => _editor)
            .Show();
    }

    private void Close()
    {
        var app = _dialog?.App;
        _dialog?.Close();
        _dialog = null;
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private bool HasUnsavedChanges()
    {
        _ = _editVersion.Value;
        return !string.Equals(GetEditorText(), _loadedText, StringComparison.Ordinal);
    }

    private string GetEditorText()
        => CodeEditorFactory.GetText(_editor);
}
