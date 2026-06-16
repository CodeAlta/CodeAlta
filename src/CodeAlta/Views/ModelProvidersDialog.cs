using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Collections;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class ModelProvidersDialog
{
    private readonly record struct ProviderConfigurationRefreshDialogResult(
        IReadOnlyList<CodeAltaProviderDocument> Definitions,
        ProviderConfigurationSaveResult RefreshResult);

    private static readonly ProviderTypeOption[] ProviderTypes =
    [
        new("openai-chat", "OpenAI Chat"),
        new("openai-responses", "OpenAI Responses"),
        new("azure-openai", "Azure OpenAI"),
        new("codex", "Codex"),
        new("copilot", "Copilot"),
        new("xai", "xAI Grok"),
        new("anthropic", "Anthropic"),
        new("google-genai", "Google GenAI"),
        new("vertex-ai", "Vertex AI"),
        new("mistral", "Mistral"),
    ];

    private static readonly ReasoningOption[] ReasoningOptions =
    [
        new("none", SR.T("None")),
        new("minimal", SR.T("Minimal")),
        new("low", SR.T("Low")),
        new("medium", SR.T("Medium")),
        new("high", SR.T("High")),
        new("xhigh", SR.T("XHigh")),
    ];

    private readonly IModelProviderDialogService _modelProviders;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly ListBox<ModelProviderEditorItemViewModel> _providerList;
    private readonly BindableList<ModelProviderEditorItemViewModel> _providers;
    private readonly State<int> _selectedProviderIndex = new(-1);
    private readonly State<int> _providerEditVersion = new(0);
    private readonly Markup _statusMarkup;
    private readonly Markup _changeSummaryMarkup;
    private readonly Button _saveButton;
    private readonly Visual _detailHost;
    private IReadOnlyList<ProviderDraftSnapshot> _loadedSnapshot = [];
    private readonly State<int> _activeOperationCount = new(0);
    private readonly State<ProviderDialogOperationKind> _activeOperationKind = new(ProviderDialogOperationKind.None);
    private readonly State<string?> _activeOperationNoticeText = new(null);
    private readonly State<string?> _activeLoginUrl = new(null);
    private readonly State<string?> _activeLoginDeviceCode = new(null);
    private readonly State<int> _activeLoginDialogRefreshVersion = new(0);
    private readonly State<bool> _hasLoadedDefinitions = new(false);
    private readonly State<string> _statusText = new($"[dim]{SR.T("Configure model providers and save to refresh the runtime.")}[/]");
    private IReadOnlyDictionary<string, ProviderRuntimeStatus> _runtimeStatuses = new Dictionary<string, ProviderRuntimeStatus>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _activeOperationCancellation;
    private Dialog? _activeLoginDialog;
    private ModelProviderModelSelectionDialog? _activeModelSelectionDialog;

    public ModelProvidersDialog(
        IModelProviderDialogService modelProviders,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(modelProviders);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _modelProviders = modelProviders;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(RequestClose);

        _providerList = new ListBox<ModelProviderEditorItemViewModel>()
            .MinWidth(28)
            .Stretch();
        _providers = _providerList.Items;
        _providerList.SelectedIndex(_selectedProviderIndex.Bind.Value);
        _providerList.ItemTemplate = new DataTemplate<ModelProviderEditorItemViewModel>(
            (DataTemplateValue<ModelProviderEditorItemViewModel> value, in DataTemplateContext _) => BuildProviderListItem(value),
            null);

        _statusMarkup = new Markup(BuildStatusMarkup)
        {
            Wrap = true,
        };
        _changeSummaryMarkup = new Markup(BuildChangeSummaryMarkup)
        {
            Wrap = false,
            VerticalAlignment = Align.Center,
        };

        _detailHost = new ComputedVisual(
            () =>
            {
                var index = _selectedProviderIndex.Value;
                return index >= 0 && index < _providers.Count
                    ? BuildDetailPane(_providers[index])
                    : BuildEmptyState();
            });

        var addButton = new Button($"{TerminalIcons.MdPlus} {SR.T("Add")}")
            .Tone(ControlTone.Primary)
            .Click(AddProvider);
        var deleteButton = new Button($"{TerminalIcons.MdDelete} {SR.T("Delete")}")
            .Tone(ControlTone.Error)
            .Click(DeleteSelectedProvider);
        var refreshButton = new Button(SR.T("Refresh"))
            .Tone(ControlTone.Warning)
            .Click(() => StartRefresh(confirmWhenDirty: true));
        var advancedButton = new Button($"{TerminalIcons.MdCodeBraces} {SR.T("Advanced TOML")}")
            .Tone(ControlTone.Default)
            .Click(OpenAdvancedEditor);
        _saveButton = new Button(SR.T("Save"))
            .Tone(ControlTone.Success)
            .IsEnabled(() => _activeOperationCount.Value == 0 && HasUnsavedChanges())
            .Click(StartSave);

        var toolbar = new HStack(addButton, deleteButton, refreshButton, advancedButton, _saveButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 1,
        };

        var header = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto });
        header.Cell(_changeSummaryMarkup, 0, 0);
        header.Cell(toolbar, 0, 1);

        var leftPane = new VStack(
            new Group(SR.T("Providers"))
                .Style(GroupStyle.Rounded)
                .Content(_providerList.Stretch())
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        var splitter = new HSplitter(
            leftPane,
            new Group(SR.T("Provider Details"))
                .Style(GroupStyle.Rounded)
                .Content(new ScrollViewer(_detailHost).Stretch())
                .Padding(1)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            Ratio = 0.28,
            MinFirst = 26,
            MinSecond = 50,
        };

        var intro = new Markup($"[dim]{SR.T("Enable the providers you want available in CodeAlta. Warnings explain why a provider may not start, Test Provider verifies current settings before you save, and Advanced TOML opens the full config editor for provider settings not exposed here.")}[/]")
        {
            Wrap = true,
        };

        var content = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        content.Cell(header, 0, 0);
        content.Cell(intro, 1, 0);
        content.Cell(splitter, 2, 0);
        content.Cell(_statusMarkup, 3, 0);

        _dialog = new Dialog()
            .Title(SR.T("Model Providers"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup(BuildBottomRightHintMarkup))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 110, minHeight: 28, widthFactor: 0.80, heightFactor: 0.80);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Manage.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the model providers dialog."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => RequestClose(),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Manage.CancelOperation",
            LabelMarkup = SR.T("Cancel Provider Operation"),
            DescriptionMarkup = SR.T("Cancel the current cancelable provider operation."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl)),
            Importance = CommandImportance.Primary,
            CanExecute = _ => IsCancelableOperationActive(),
            IsVisible = _ => IsDialogOperationActive(),
            ConsumesGestureWhenUnavailable = false,
            Execute = _ => CancelActiveOperation(),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Manage.CopyLoginUrl",
            LabelMarkup = SR.T("Copy Login URL"),
            DescriptionMarkup = SR.T("Copy the current login URL to the clipboard."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlU, TerminalModifiers.Ctrl)),
            Importance = CommandImportance.Secondary,
            CanExecute = _ => !string.IsNullOrWhiteSpace(_activeLoginUrl.Value),
            IsVisible = _ => IsDialogOperationActive(),
            ConsumesGestureWhenUnavailable = false,
            Execute = _ => CopyActiveLoginUrl(),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Manage.CopyDeviceCode",
            LabelMarkup = SR.T("Copy Device Code"),
            DescriptionMarkup = SR.T("Copy the current device login code to the clipboard."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlD, TerminalModifiers.Ctrl)),
            Importance = CommandImportance.Secondary,
            CanExecute = _ => !string.IsNullOrWhiteSpace(_activeLoginDeviceCode.Value),
            IsVisible = _ => IsDialogOperationActive(),
            ConsumesGestureWhenUnavailable = false,
            Execute = _ => CopyActiveLoginDeviceCode(),
        });
    }

    public void Show()
    {
        if (_dialog.App is not null)
        {
            return;
        }

        _dialog.Show();
        FocusProviderList();
        if (!_hasLoadedDefinitions.Value)
        {
            StartReload(confirmWhenDirty: false);
        }
    }

    public bool IsOpen => _dialog.App is not null;

    public void Refresh()
        => StartRefresh(confirmWhenDirty: true);

    private void StartReload(bool confirmWhenDirty)
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("reload provider configuration"));
            return;
        }

        if (confirmWhenDirty && HasUnsavedChanges())
        {
            new ConfirmationDialog(
                SR.T("Reload Provider Configuration?"),
                [SR.T("Discard unsaved provider changes and reload from disk?")],
                SR.T("Reload"),
                ControlTone.Warning,
                () =>
                {
                    StartReload(confirmWhenDirty: false);
                    return Task.CompletedTask;
                },
                _getBounds,
                _getFocusTarget)
                .Show();
            return;
        }

        if (!TryBeginDialogOperation(SR.T("reload provider configuration")))
        {
            return;
        }

        try
        {
            SetStatus($"[primary]{SR.T("Loading provider configuration...")}[/]");
            QueueBackgroundOperation(
                _modelProviders.LoadDefinitions,
                definitions => LoadDefinitionsIntoDialog(
                    definitions,
                    emptyStatusText: $"[warning]{SR.T("No providers are configured yet. Add one, or enable Codex/Copilot.")}[/]",
                    loadedStatusText: $"[dim]{SR.T("Provider configuration loaded from disk.")}[/]"),
                ex => SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]"));
        }
        catch
        {
            EndDialogOperation();
            throw;
        }
    }

    private void StartRefresh(bool confirmWhenDirty)
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("refresh provider configuration"));
            return;
        }

        if (confirmWhenDirty && HasUnsavedChanges())
        {
            new ConfirmationDialog(
                SR.T("Refresh Provider Configuration?"),
                [SR.T("Discard unsaved provider changes, reload from disk, and retest saved provider availability?")],
                SR.T("Refresh"),
                ControlTone.Warning,
                () =>
                {
                    StartRefresh(confirmWhenDirty: false);
                    return Task.CompletedTask;
                },
                _getBounds,
                _getFocusTarget)
                .Show();
            return;
        }

        if (!TryBeginDialogOperation(SR.T("refresh provider configuration")))
        {
            return;
        }

        try
        {
            SetStatus($"[primary]{SR.T("Refreshing provider configuration and testing availability...")}[/]");
            MarkEnabledProvidersRefreshInProgress();
            QueueBackgroundOperation(
                async cancellationToken =>
                {
                    var refreshResult = await _modelProviders.RefreshConfigurationAsync(cancellationToken);
                    var definitions = _modelProviders.LoadDefinitions();
                    return new ProviderConfigurationRefreshDialogResult(definitions, refreshResult);
                },
                result =>
                {
                    var loadedStatusText = result.RefreshResult.RuntimeRefreshSucceeded
                        ? $"[success]{SR.T("Provider configuration refreshed from disk and availability tested.")}[/]"
                        : $"[warning]{SR.T("Provider configuration refreshed from disk, but runtime refresh failed: {0}", AnsiMarkup.Escape(result.RefreshResult.RuntimeRefreshErrorMessage ?? SR.T("unknown error")))}[/]";
                    LoadDefinitionsIntoDialog(
                        result.Definitions,
                        emptyStatusText: $"[warning]{SR.T("No providers are configured yet. Add one, or enable Codex/Copilot.")}[/]",
                        loadedStatusText);
                },
                ex =>
                {
                    if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)
                    {
                        ClearProviderRefreshInProgress();
                        SetStatus($"[warning]{SR.T("Provider refresh canceled.")}[/]");
                        return;
                    }

                    ClearProviderRefreshInProgress();
                    SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]");
                });
        }
        catch
        {
            EndDialogOperation();
            throw;
        }
    }

    private void AddProvider()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("add a provider"));
            return;
        }

        var nextIndex = 1;
        string providerKey;
        do
        {
            providerKey = $"provider-{nextIndex++}";
        }
        while (_providers.Any(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)));

        var item = CreateEditorItem(ModelProviderEditorItemViewModel.Create(providerKey));
        _providers.Add(item);
        SetSelectedProviderIndex(_providers.Count - 1);
        NotifyProviderDraftChanged();
        SetStatus($"[warning]{SR.T("Added provider. Configure required fields, then Save to apply.")}[/]");
    }

    private void DeleteSelectedProvider()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("delete a provider"));
            return;
        }

        var item = GetSelectedItem();
        if (item is null)
        {
            SetStatus($"[warning]{SR.T("Select a provider to delete.")}[/]");
            return;
        }

        if (item.IsReserved)
        {
            SetStatus($"[warning]{SR.T("Reserved providers cannot be deleted. Disable them instead.")}[/]");
            return;
        }

        _providers.Remove(item);
        SetSelectedProviderIndex(Math.Clamp(_selectedProviderIndex.Value, 0, _providers.Count - 1));
        NotifyProviderDraftChanged();
        SetStatus($"[warning]{SR.T("Removed provider. Save to apply, or Refresh to reload from disk.")}[/]");
    }

    private void StartSave()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("save provider changes"));
            return;
        }

        if (!TryBuildDefinitions(out var definitions, out var errorMessage))
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(errorMessage)}[/]");
            return;
        }

        if (!HasUnsavedChanges())
        {
            SetStatus($"[dim]{SR.T("No provider changes to save.")}[/]");
            return;
        }

        if (!TryBeginDialogOperation(SR.T("save provider changes")))
        {
            return;
        }

        SetStatus($"[primary]{SR.T("Saving provider configuration...")}[/]");
        QueueBackgroundOperation(
            async () =>
            {
                var saveResult = await _modelProviders.SaveDefinitionsAsync(definitions);
                return new ProviderDefinitionsSaveDialogResult(_modelProviders.LoadDefinitions(), saveResult);
            },
            result =>
            {
                LoadDefinitionsIntoDialog(
                    result.DefinitionsFromDisk,
                    emptyStatusText: $"[warning]{SR.T("No providers are configured yet. Add one, or enable Codex/Copilot.")}[/]",
                    loadedStatusText: FormatProviderSaveStatus(result.SaveResult));
            },
            ex => SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]"));
    }

    private void OpenAdvancedEditor()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("open Advanced TOML"));
            return;
        }

        if (HasUnsavedChanges())
        {
            SetStatus($"[warning]{SR.T("Save or Refresh the form changes before opening Advanced TOML so the code editor starts from the on-disk config.")}[/]");
            return;
        }

        try
        {
            new ConfigAdvancedEditorDialog(
                _modelProviders,
                _modelProviders.ConfigurationPath,
                _modelProviders.LoadConfigurationContent(),
                OnAdvancedEditorSaved,
                _getBounds,
                () => _dialog)
                .Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]");
        }
    }

    private void OnAdvancedEditorSaved(ProviderConfigurationSaveResult saveResult)
    {
        try
        {
            LoadDefinitionsIntoDialog(
                _modelProviders.LoadDefinitions(),
                emptyStatusText: $"[warning]{SR.T("Provider configuration saved, but no providers are configured yet. Add one, or enable Codex/Copilot.")}[/]",
                loadedStatusText: FormatProviderSaveStatus(saveResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"[error]{SR.T("Saved TOML, but failed to reload provider configuration: {0}", AnsiMarkup.Escape(ex.GetBaseException().Message))}[/]");
        }
    }

    private void StartTest(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("test a provider"));
            return;
        }

        if (!_providers.Contains(item))
        {
            SetStatus($"[warning]{SR.T("Select a provider to test.")}[/]");
            return;
        }

        if (!TryBuildDefinition(item, out var definition, out var errorMessage))
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(errorMessage)}[/]");
            return;
        }

        if (!TryBeginDialogOperation(SR.T("test a provider")))
        {
            return;
        }

        SetStatus($"[primary]{SR.T("Testing {0}...", AnsiMarkup.Escape(item.Label))}[/]");
        item.SetTestInProgress(SR.T("Testing provider connectivity and model discovery..."));
        QueueBackgroundOperation(
            cancellationToken => _modelProviders.TestProviderAsync(definition, cancellationToken),
            result =>
            {
                if (_providers.Contains(item))
                {
                    var enabledAfterTest = result.Success && item.SetSuccessfulResultAndEnable(result.Message);
                    if (!result.Success)
                    {
                        item.SetTestResult(success: false, result.Message);
                    }

                    var statusSuffix = enabledAfterTest
                        ? $"{Environment.NewLine}[warning]{SR.T("{0} was enabled automatically after the successful test. Save to refresh the runtime with this provider.", AnsiMarkup.Escape(item.Label))}[/]"
                        : string.Empty;

                    SetStatus(result.Success
                        ? $"[success]{AnsiMarkup.Escape(result.Message)}[/]{statusSuffix}"
                        : $"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
                    return;
                }

                SetStatus(result.Success
                    ? $"[success]{AnsiMarkup.Escape(result.Message)}[/]"
                    : $"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
            },
            ex =>
            {
                if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)
                {
                    item.ClearTestResult();
                    SetStatus($"[warning]{SR.T("Provider test canceled.")}[/]");
                    return;
                }

                var message = ex.GetBaseException().Message;
                if (_providers.Contains(item))
                {
                    item.SetTestResult(success: false, message);
                }

                SetStatus($"[error]{AnsiMarkup.Escape(message)}[/]");
            });
    }

    private void StartProviderAction(
        ModelProviderEditorItemViewModel item,
        string operationDescription,
        string progressMessage,
        Func<CodeAltaProviderDocument, Task<ProviderTestResult>> actionAsync)
    {
        StartProviderAction(
            item,
            operationDescription,
            progressMessage,
            ProviderDialogOperationKind.None,
            canCancel: false,
            (definition, _, _) => actionAsync(definition));
    }

    private void StartProviderAction(
        ModelProviderEditorItemViewModel item,
        string operationDescription,
        string progressMessage,
        Func<CodeAltaProviderDocument, Action<string>, Task<ProviderTestResult>> actionAsync)
    {
        StartProviderAction(
            item,
            operationDescription,
            progressMessage,
            ProviderDialogOperationKind.None,
            canCancel: false,
            (definition, reportStatus, _) => actionAsync(definition, reportStatus));
    }

    private void StartProviderAction(
        ModelProviderEditorItemViewModel item,
        string operationDescription,
        string progressMessage,
        ProviderDialogOperationKind operationKind,
        bool canCancel,
        Func<CodeAltaProviderDocument, Action<string>, CancellationToken, Task<ProviderTestResult>> actionAsync)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(progressMessage);
        ArgumentNullException.ThrowIfNull(actionAsync);

        operationDescription = SR.T(operationDescription);
        progressMessage = SR.T(progressMessage);

        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(operationDescription);
            return;
        }

        if (!_providers.Contains(item))
        {
            SetStatus($"[warning]{SR.T("Select a provider to {0}.", AnsiMarkup.Escape(operationDescription))}[/]");
            return;
        }

        if (!TryBuildDefinition(item, out var definition, out var errorMessage))
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(errorMessage)}[/]");
            return;
        }

        if (!TryBeginDialogOperation(operationDescription, operationKind, canCancel))
        {
            return;
        }

        SetStatus($"[primary]{AnsiMarkup.Escape(progressMessage)}[/]");
        QueueBackgroundOperation(
            cancellationToken => actionAsync(
                definition,
                message => _ = _dialog.Dispatcher.InvokeAsync(
                    () =>
                    {
                        CaptureActiveLoginDetails(message);
                        SetStatus($"[primary]{AnsiMarkup.Escape(message)}[/]");
                    }),
                cancellationToken),
            result =>
            {
                if (_providers.Contains(item))
                {
                    var enabledAfterLogin = result.Success && IsLoginOperation(operationKind)
                        ? item.SetSuccessfulResultAndEnable(result.Message)
                        : false;
                    if (!result.Success || !IsLoginOperation(operationKind))
                    {
                        item.SetTestResult(result.Success, result.Message);
                    }

                    var statusSuffix = enabledAfterLogin
                        ? $"{Environment.NewLine}[warning]{SR.T("{0} was enabled automatically after login. Save to refresh the runtime with this provider.", AnsiMarkup.Escape(item.Label))}[/]"
                        : string.Empty;

                    SetStatus(result.Success
                        ? $"[success]{AnsiMarkup.Escape(result.Message)}[/]{statusSuffix}"
                        : $"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
                    return;
                }

                SetStatus(result.Success
                    ? $"[success]{AnsiMarkup.Escape(result.Message)}[/]"
                    : $"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
            },
            ex =>
            {
                if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)
                {
                    SetStatus($"[warning]{SR.T("Provider operation canceled.")}[/]");
                    return;
                }

                var message = ex.GetBaseException().Message;
                if (_providers.Contains(item))
                {
                    item.SetTestResult(success: false, message);
                }

                SetStatus($"[error]{AnsiMarkup.Escape(message)}[/]");
            });
    }

    private Visual BuildDetailPane(ModelProviderEditorItemViewModel item)
    {
        var bindings = GetBindings(item);
        var title = new Markup(() => BuildSelectedTitleMarkup(item))
        {
            Wrap = true,
        };
        var summary = new Markup(() => BuildSelectedSummaryMarkup(item))
        {
            Wrap = true,
        };
        var availability = new Markup(() => BuildAvailabilityMarkup(item))
        {
            Wrap = true,
        };

        var testButton = new Button(SR.T("Test Provider"))
            .Tone(ControlTone.Primary)
            .Click(() => StartTest(item));
        var codexActions = item.ProviderType == "codex"
            ? CreateCodexSubscriptionActions(item)
            : null;
        var copilotDirectActions = item.ProviderType == "copilot"
            ? CreateCopilotDirectActions(item)
            : null;
        var xaiDirectActions = item.ProviderType == "xai"
            ? CreateXaiDirectActions(item)
            : null;

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                RowGap = 1,
            }
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto });

        var row = 0;
        AddTextRow(form, ref row, SR.T("Provider Key"), CreateKeyField(item), CreateSpacer());
        AddSelectRow(form, ref row, SR.T("Type"), CreateTypeSelect(item), CreateSpacer());
        AddCheckRow(form, ref row, SR.T("Available"), CreateEnabledCheckBox(item), CreateSpacer());
        AddTextRow(form, ref row, SR.T("Display Name"), CreateDefaultTextField(bindings.DisplayName, () => item.UseDefaultDisplayName), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultDisplayName));
        AddTextRow(form, ref row, SR.T("Model"), CreateModelField(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultModel));
        AddSelectRow(form, ref row, SR.T("Reasoning"), CreateReasoningSelect(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultReasoningEffort));

        if (item.ProviderType is "openai-chat" or "openai-responses" or "azure-openai" or "anthropic" or "google-genai" or "mistral")
        {
            AddTextRow(form, ref row, SR.T("API Key"), CreateApiKeyBox(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultApiKey));
            AddTextRow(form, ref row, SR.T("API Key Env"), CreateApiKeyEnvField(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultApiKeyEnv));
        }

        if (item.ProviderType is "openai-chat" or "openai-responses" or "azure-openai" or "codex" or "copilot" or "anthropic" or "google-genai" or "vertex-ai" or "xai" or "mistral")
        {
            AddTextRow(form, ref row, SR.T("API URL"), CreateApiUrlField(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultApiUrl));
        }

        if (item.ProviderType is "openai-chat" or "openai-responses")
        {
            AddTextRow(form, ref row, SR.T("Organization"), CreateDefaultTextField(bindings.OrganizationId, () => item.UseDefaultOrganizationId), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultOrganizationId));
            AddTextRow(form, ref row, SR.T("Project Id"), CreateDefaultTextField(bindings.ProjectId, () => item.UseDefaultProjectId), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultProjectId));
        }

        if (item.ProviderType == "vertex-ai")
        {
            AddTextRow(form, ref row, SR.T("Project"), CreateVertexProjectField(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultProject));
            AddTextRow(form, ref row, SR.T("Location"), CreateVertexLocationField(item), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultLocation));
        }

        if (item.ProviderType is "openai-chat" or "openai-responses" or "azure-openai" or "codex" or "copilot" or "anthropic" or "google-genai" or "vertex-ai" or "xai" or "mistral")
        {
            AddTextRow(form, ref row, SR.T("Models.dev Id"), CreateDefaultTextField(bindings.ModelsDevProviderId, () => item.UseDefaultModelsDevProviderId), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultModelsDevProviderId));
            AddTextRow(form, ref row, SR.T("Single Model Id"), CreateDefaultTextField(bindings.SingleModelId, () => item.UseDefaultSingleModelId), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultSingleModelId));
        }

        if (item.ProviderType == "codex")
        {
            AddTextRow(form, ref row, SR.T("Auth Source"), CreateDefaultTextField(bindings.AuthSource, () => item.UseDefaultAuthSource), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultAuthSource));
            AddTextRow(form, ref row, SR.T("Account/Workspace Id"), CreateDefaultTextField(bindings.AccountId, () => item.UseDefaultAccountId), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultAccountId));
            AddTextRow(form, ref row, SR.T("Model Discovery"), CreateDefaultTextField(bindings.ModelDiscovery, () => item.UseDefaultModelDiscovery), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultModelDiscovery));
            AddTextRow(form, ref row, SR.T("Response Transport"), CreateDefaultTextField(bindings.ResponseTransport, () => item.UseDefaultResponseTransport), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultResponseTransport));
        }
        else if (item.ProviderType == "copilot")
        {
            AddTextRow(form, ref row, SR.T("Auth Source"), CreateDefaultTextField(bindings.AuthSource, () => item.UseDefaultAuthSource), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultAuthSource));
            AddTextRow(form, ref row, SR.T("GitHub Enterprise"), CreateDefaultTextField(bindings.GitHubEnterpriseUrl, () => item.UseDefaultGitHubEnterpriseUrl), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultGitHubEnterpriseUrl));
            AddTextRow(form, ref row, SR.T("Model Discovery"), CreateDefaultTextField(bindings.ModelDiscovery, () => item.UseDefaultModelDiscovery), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultModelDiscovery));
        }
        else if (item.ProviderType == "xai")
        {
            AddTextRow(form, ref row, SR.T("Auth Source"), CreateDefaultTextField(bindings.AuthSource, () => item.UseDefaultAuthSource), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultAuthSource));
            AddTextRow(form, ref row, SR.T("Model Discovery"), CreateDefaultTextField(bindings.ModelDiscovery, () => item.UseDefaultModelDiscovery), CreateDefaultCheckBox(SR.T("Default"), bindings.UseDefaultModelDiscovery));
        }

        var advancedNotice = new Markup($"[dim]{SR.T("Advanced provider TOML sections such as profile, compaction, extra_body, and model_overrides are preserved when you save from this form. Use Advanced TOML to edit them directly.")}[/]")
        {
            Wrap = true,
        };

        var detailContent = new List<Visual>
        {
            title,
            CreateSectionRule(SR.T("Availability")),
            availability,
            CreateAvailabilityToggleButton(item),
            summary,
            testButton,
        };
        if (codexActions is not null)
        {
            detailContent.Add(codexActions);
        }

        if (copilotDirectActions is not null)
        {
            detailContent.Add(copilotDirectActions);
        }

        if (xaiDirectActions is not null)
        {
            detailContent.Add(xaiDirectActions);
        }

        detailContent.Add(form);
        detailContent.Add(advancedNotice);

        return new VStack(detailContent.ToArray())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };
    }

    private Visual CreateKeyField(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).ProviderKey;
        return CreateValidationField(
            new TextBox(binding)
                .IsEnabled(!item.IsReserved),
            () => ModelProviderEditorDiagnostics.ValidateProviderKey(item, _providers));
    }

    private Button CreateCancelableProviderActionButton(
        ModelProviderEditorItemViewModel item,
        string label,
        string cancelLabel,
        ProviderDialogOperationKind operationKind,
        string operationDescription,
        string progressMessage,
        Func<CodeAltaProviderDocument, Action<string>, CancellationToken, Task<ProviderTestResult>> actionAsync)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(progressMessage);
        ArgumentNullException.ThrowIfNull(actionAsync);

        return new Button(() => new TextBlock(IsActiveOperation(operationKind) ? cancelLabel : label))
            .Tone(() => IsActiveOperation(operationKind) ? ControlTone.Warning : ControlTone.Primary)
            .Click(
                () =>
                {
                    if (IsActiveOperation(operationKind))
                    {
                        CancelActiveOperation();
                        return;
                    }

                    StartProviderAction(
                        item,
                        operationDescription,
                        progressMessage,
                        operationKind,
                        canCancel: true,
                        actionAsync);
                });
    }

    private Visual CreateCodexSubscriptionActions(ModelProviderEditorItemViewModel item)
        => new VStack(
            new Markup($"[dim]{SR.T("ChatGPT/Codex subscription actions never send a model turn. Use Account/Workspace Id to pin a specific account when required.")}[/]") { Wrap = true },
            new HStack(
                CreateCancelableProviderActionButton(
                    item,
                    SR.T("Browser Login"),
                    SR.T("Cancel Browser Login"),
                    ProviderDialogOperationKind.CodexBrowserLogin,
                    SR.T("start ChatGPT browser login"),
                    SR.T("Starting ChatGPT browser login..."),
                    _modelProviders.LoginWithBrowserAsync),
                CreateCancelableProviderActionButton(
                    item,
                    SR.T("Device Login"),
                    SR.T("Cancel Device Login"),
                    ProviderDialogOperationKind.CodexDeviceLogin,
                    SR.T("start ChatGPT device-code login"),
                    SR.T("Requesting ChatGPT device code..."),
                    _modelProviders.LoginWithDeviceCodeAsync),
                new Button(SR.T("Test Auth"))
                    .Tone(ControlTone.Primary)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("test ChatGPT authentication"),
                        SR.T("Testing ChatGPT authentication without sending a model turn..."),
                        definition => _modelProviders.TestAuthenticationAsync(definition))),
                new Button(SR.T("List Models"))
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("list Codex subscription models"),
                        SR.T("Listing Codex subscription models without sending a model turn..."),
                        definition => _modelProviders.ListModelsAsync(definition))),
                new Button(SR.T("List Accounts"))
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("list ChatGPT accounts/workspaces"),
                        SR.T("Reading ChatGPT account/workspace metadata..."),
                        definition => _modelProviders.ListAccountsAsync(definition))),
                new Button(SR.T("Logout"))
                    .Tone(ControlTone.Error)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("logout ChatGPT credentials"),
                        SR.T("Deleting CodeAlta-owned ChatGPT/Codex credentials..."),
                        definition => _modelProviders.LogoutAsync(definition))))
            {
                Spacing = 1,
            })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };

    private Visual CreateCopilotDirectActions(ModelProviderEditorItemViewModel item)
        => new VStack(
            new Markup($"[dim]{SR.T("Copilot login opens GitHub's device authorization page, then stores CodeAlta-owned GitHub/Copilot tokens for this provider. No model turn is sent.")}[/]") { Wrap = true },
            new HStack(
                CreateCancelableProviderActionButton(
                    item,
                    SR.T("Browser Login"),
                    SR.T("Cancel Browser Login"),
                    ProviderDialogOperationKind.CopilotBrowserLogin,
                    SR.T("start Copilot browser login"),
                    SR.T("Requesting Copilot login code..."),
                    _modelProviders.LoginWithBrowserAsync),
                CreateCancelableProviderActionButton(
                    item,
                    SR.T("Device Login"),
                    SR.T("Cancel Device Login"),
                    ProviderDialogOperationKind.CopilotDeviceLogin,
                    SR.T("start Copilot device-code login"),
                    SR.T("Requesting Copilot device code..."),
                    _modelProviders.LoginWithDeviceCodeAsync),
                new Button(SR.T("Test Auth"))
                    .Tone(ControlTone.Primary)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("test Copilot authentication"),
                        SR.T("Checking cached Copilot credentials..."),
                        definition => _modelProviders.TestAuthenticationAsync(definition))),
                new Button(SR.T("List Models"))
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("list Copilot models"),
                        SR.T("Listing Copilot models without sending a model turn..."),
                        definition => _modelProviders.ListModelsAsync(definition))),
                new Button(SR.T("Logout"))
                    .Tone(ControlTone.Error)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("logout Copilot credentials"),
                        SR.T("Deleting CodeAlta-owned Copilot credentials..."),
                        definition => _modelProviders.LogoutAsync(definition))))
            {
                Spacing = 1,
            })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };

    private Visual CreateXaiDirectActions(ModelProviderEditorItemViewModel item)
        => new VStack(
            new Markup($"[dim]{SR.T("xAI Grok login uses the public Grok-CLI OAuth client and stores CodeAlta-owned access + refresh tokens for this provider. Browser login opens auth.x.ai for PKCE; device login prints a code for headless hosts. No model turn is sent.")}[/]") { Wrap = true },
            new HStack(
                CreateCancelableProviderActionButton(
                    item,
                    SR.T("Browser Login"),
                    SR.T("Cancel Browser Login"),
                    ProviderDialogOperationKind.XaiBrowserLogin,
                    SR.T("start xAI browser login"),
                    SR.T("Starting xAI browser login..."),
                    _modelProviders.LoginWithBrowserAsync),
                CreateCancelableProviderActionButton(
                    item,
                    SR.T("Device Login"),
                    SR.T("Cancel Device Login"),
                    ProviderDialogOperationKind.XaiDeviceLogin,
                    SR.T("start xAI device-code login"),
                    SR.T("Requesting xAI device code..."),
                    _modelProviders.LoginWithDeviceCodeAsync),
                new Button(SR.T("Test Auth"))
                    .Tone(ControlTone.Primary)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("test xAI authentication"),
                        SR.T("Checking cached xAI credentials..."),
                        definition => _modelProviders.TestAuthenticationAsync(definition))),
                new Button(SR.T("List Models"))
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("list xAI models"),
                        SR.T("Listing xAI models without sending a model turn..."),
                        definition => _modelProviders.ListModelsAsync(definition))),
                new Button(SR.T("Logout"))
                    .Tone(ControlTone.Error)
                    .Click(() => StartProviderAction(
                        item,
                        SR.T("logout xAI credentials"),
                        SR.T("Deleting CodeAlta-owned xAI credentials..."),
                        definition => _modelProviders.LogoutAsync(definition))))
            {
                Spacing = 1,
            })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };

    private Select<ProviderTypeOption> CreateTypeSelect(ModelProviderEditorItemViewModel item)
    {
        var select = new Select<ProviderTypeOption>()
            .MinWidth(22)
            .IsEnabled(!item.IsReserved);
        foreach (var option in ProviderTypes)
        {
            select.Items.Add(option);
        }

        select.SelectedIndex = Math.Max(0, Array.FindIndex(ProviderTypes, option => string.Equals(option.Id, item.ProviderType, StringComparison.Ordinal)));
        select.SelectionChanged(
            (_, e) =>
            {
                if ((uint)e.NewIndex >= (uint)ProviderTypes.Length)
                {
                    return;
                }

                item.ProviderType = ProviderTypes[e.NewIndex].Id;
            });
        return select;
    }

    private CheckBox CreateEnabledCheckBox(ModelProviderEditorItemViewModel item)
        => new CheckBox(SR.T("Enable this provider")).IsChecked(GetBindings(item).Enabled);

    private Button CreateAvailabilityToggleButton(ModelProviderEditorItemViewModel item)
        => new Button(() => new TextBlock(item.Enabled
                ? $"{TerminalIcons.MdPauseCircleOutline} {SR.T("Disable Provider")}"
                : $"{TerminalIcons.MdCheckCircleOutline} {SR.T("Enable Provider")}"))
            .Tone(() => item.Enabled ? ControlTone.Warning : ControlTone.Success)
            .IsEnabled(!item.IsReserved)
            .Click(
                () =>
                {
                    if (IsDialogOperationActive())
                    {
                        ReportActiveOperationBlock(item.Enabled ? SR.T("disable this provider") : SR.T("enable this provider"));
                        return;
                    }

                    item.Enabled = !item.Enabled;
                    SetStatus(item.Enabled
                        ? $"[warning]{SR.T("{0} is enabled. Save to refresh the runtime with this provider.", AnsiMarkup.Escape(item.Label))}[/]"
                        : $"[warning]{SR.T("{0} is disabled. Save to remove it from runtime selection.", AnsiMarkup.Escape(item.Label))}[/]");
                });

    private Visual CreateModelField(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).Model;
        var selectorButton = new Button(SR.T("Models"))
            .Tone(ControlTone.Default)
            .IsEnabled(() => !item.UseDefaultModel && !IsDialogOperationActive())
            .Click(() => StartModelSelection(item));

        return new HStack(
            selectorButton,
            new TextBox(binding)
                .IsEnabled(() => !item.UseDefaultModel)
                .Stretch())
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };
    }

    private void StartModelSelection(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("list provider models"));
            return;
        }

        if (item.UseDefaultModel)
        {
            SetStatus($"[warning]{SR.T("Uncheck Model Default before choosing a provider model.")}[/]");
            return;
        }

        if (!_providers.Contains(item))
        {
            SetStatus($"[warning]{SR.T("Select a provider before choosing a model.")}[/]");
            return;
        }

        if (_activeModelSelectionDialog?.IsOpen == true)
        {
            _activeModelSelectionDialog.Focus();
            return;
        }

        if (!TryBuildDefinition(item, out var definition, out var errorMessage))
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(errorMessage)}[/]");
            return;
        }

        if (!TryBeginDialogOperation(SR.T("list provider models"), canCancel: true))
        {
            return;
        }

        SetStatus($"[primary]{SR.T("Listing models for {0}...", AnsiMarkup.Escape(item.Label))}[/]");
        QueueBackgroundOperation(
            cancellationToken => _modelProviders.ListSelectableModelsAsync(definition, cancellationToken),
            result => CompleteModelSelectionListing(item, result),
            ex =>
            {
                if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)
                {
                    SetStatus($"[warning]{SR.T("Model listing canceled.")}[/]");
                    return;
                }

                SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]");
            });
    }

    private void CompleteModelSelectionListing(ModelProviderEditorItemViewModel item, ProviderModelListResult result)
    {
        if (!_providers.Contains(item))
        {
            SetStatus($"[warning]{SR.T("The provider was removed before model listing completed.")}[/]");
            return;
        }

        if (!result.Success)
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
            return;
        }

        if (result.Models.Count == 0)
        {
            SetStatus($"[warning]{SR.T("No models were returned for this provider.")}[/]");
            return;
        }

        _activeModelSelectionDialog = new ModelProviderModelSelectionDialog(
            item.Label,
            result.Models,
            item.Model,
            modelId => SelectModel(item, modelId),
            _getBounds,
            () => _dialog);
        _activeModelSelectionDialog.Show();
        SetStatus($"[success]{AnsiMarkup.Escape(result.Message)} {SR.T("Select a model from the popup.")}[/]");
    }

    private void SelectModel(ModelProviderEditorItemViewModel item, string modelId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (!_providers.Contains(item))
        {
            SetStatus($"[warning]{SR.T("The provider was removed before the selected model could be applied.")}[/]");
            return;
        }

        item.Model = modelId;
        SetStatus($"[warning]{SR.T("Selected model {0}. Save to apply the provider change.", AnsiMarkup.Escape(modelId))}[/]");
    }

    private Visual CreateReasoningSelect(ModelProviderEditorItemViewModel item)
    {
        var select = new Select<ReasoningOption>()
            .MinWidth(16)
            .IsEnabled(() => !item.UseDefaultReasoningEffort);
        foreach (var option in ReasoningOptions)
        {
            select.Items.Add(option);
        }

        select.SelectedIndex = Math.Max(0, Array.FindIndex(ReasoningOptions, option => string.Equals(option.Value, item.ReasoningEffort, StringComparison.Ordinal)));
        select.SelectionChanged(
            (_, e) =>
            {
                if ((uint)e.NewIndex >= (uint)ReasoningOptions.Length)
                {
                    return;
                }

                item.ReasoningEffort = ReasoningOptions[e.NewIndex].Value;
            });
        return select;
    }

    private Visual CreateApiKeyBox(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).ApiKey;
        var editor = new TextBox(binding)
            .IsPassword(true)
            .PasswordRevealMode(PasswordRevealMode.WhileFocused)
            .IsEnabled(() => !item.UseDefaultApiKey);
        return CreateValidationField(editor, () => ModelProviderEditorDiagnostics.ValidateApiKey(item));
    }

    private Visual CreateApiUrlField(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).ApiUrl;
        return CreateValidationField(
            new TextBox(binding)
                .IsEnabled(() => !item.UseDefaultApiUrl),
            () => ModelProviderEditorDiagnostics.ValidateApiUrl(item) ??
                  BuildCustomApiUrlGuidance(item));
    }

    private Visual CreateApiKeyEnvField(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).ApiKeyEnv;
        return CreateValidationField(
            CreateDefaultTextField(binding, () => item.UseDefaultApiKeyEnv),
            () => ModelProviderEditorDiagnostics.ValidateApiKeyEnv(item));
    }

    private Visual CreateVertexProjectField(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).Project;
        return CreateValidationField(
            CreateDefaultTextField(binding, () => item.UseDefaultProject),
            () => ModelProviderEditorDiagnostics.ValidateVertexProject(item));
    }

    private Visual CreateVertexLocationField(ModelProviderEditorItemViewModel item)
    {
        var binding = GetBindings(item).Location;
        return CreateValidationField(
            CreateDefaultTextField(binding, () => item.UseDefaultLocation),
            () => ModelProviderEditorDiagnostics.ValidateVertexLocation(item));
    }

    private static Visual CreateDefaultTextField(Binding<string?> binding, Func<bool> getUseDefault)
        => new TextBox(binding)
            .IsEnabled(() => !getUseDefault())
            .Stretch();

    private static CheckBox CreateDefaultCheckBox(string label, Binding<bool> binding)
        => new CheckBox(SR.T(label)).IsChecked(binding);

    private static Visual CreateSpacer()
        => new TextBlock(string.Empty);

    private ValidationPresenter CreateValidationField(Visual content, Func<ValidationMessage?> getMessage)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(getMessage);

        ValidationMessage? cachedMessage = null;
        ValidationSeverity cachedSeverity = default;
        string? cachedText = null;

        return new ValidationPresenter(content)
            .Placement(ValidationPlacement.Below)
            .Message(() =>
            {
                var nextMessage = getMessage();
                if (nextMessage is null)
                {
                    cachedMessage = null;
                    cachedText = null;
                    return null;
                }

                var text = GetValidationMessageText(nextMessage.Value);
                if (cachedMessage is not null &&
                    cachedSeverity == nextMessage.Value.Severity &&
                    string.Equals(cachedText, text, StringComparison.Ordinal))
                {
                    return cachedMessage;
                }

                cachedSeverity = nextMessage.Value.Severity;
                cachedText = text;
                cachedMessage = new ValidationMessage(
                    nextMessage.Value.Severity,
                    new TextBlock(text)
                    {
                        Wrap = true,
                    });
                return cachedMessage;
            });
    }

    private static void AddTextRow(Grid form, ref int row, string label, Visual content, Visual trailing)
    {
        EnsureRow(form, row);
        form.Cell(new TextBlock(SR.T(label)) { VerticalAlignment = Align.Center }, row, 0);
        form.Cell(content.Stretch(), row, 1);
        form.Cell(trailing, row, 2);
        row++;
    }

    private static void AddSelectRow(Grid form, ref int row, string label, Visual content, Visual trailing)
        => AddTextRow(form, ref row, label, content, trailing);

    private static void AddCheckRow(Grid form, ref int row, string label, Visual content, Visual trailing)
        => AddTextRow(form, ref row, label, content, trailing);

    private static void EnsureRow(Grid form, int row)
    {
        while (form.RowDefinitions.Count <= row)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }

    private string BuildSelectedTitleMarkup(ModelProviderEditorItemViewModel item)
    {
        var diagnostics = Analyze(item);
        var (tone, icon) = GetStatusToneAndIcon(diagnostics.StatusKind);
        return $"[{tone}]{icon} {AnsiMarkup.Escape(item.Label)}[/] [dim]· {AnsiMarkup.Escape(diagnostics.StatusText)}[/]";
    }

    private string BuildSelectedSummaryMarkup(ModelProviderEditorItemViewModel item)
    {
        var diagnostics = Analyze(item);
        if (diagnostics.Entries.Count == 0)
        {
            return item.Enabled
                ? $"[primary]{SR.T("Configured.")}[/] {SR.T("Use [bold]Test Provider[/] to confirm this provider works before saving.")}"
                : SR.T("This provider is disabled. Enable it when you are ready to use it.");
        }

        return string.Join(
            Environment.NewLine,
            diagnostics.Entries
                .Distinct()
                .Select(entry =>
                {
                    var tone = entry.Severity switch
                    {
                        ValidationSeverity.Error => "error",
                        ValidationSeverity.Warning => "warning",
                        _ => "primary",
                    };
                    var icon = entry.Severity switch
                    {
                        ValidationSeverity.Error => $"{TerminalIcons.MdCloseCircleOutline}",
                        ValidationSeverity.Warning => $"{TerminalIcons.MdAlertOutline}",
                        _ => $"{TerminalIcons.MdInformationOutline}",
                    };
                    return $"[{tone}]{icon} {AnsiMarkup.Escape(entry.Message)}[/]";
                }));
    }

    private static string BuildAvailabilityMarkup(ModelProviderEditorItemViewModel item)
    {
        var (tone, icon) = GetAvailabilityToneAndIcon(item.Enabled);
        var stateText = item.Enabled ? SR.T("Enabled") : SR.T("Disabled");
        var guidance = item.Enabled
            ? SR.T("This provider can appear in the provider picker after you save.")
            : SR.T("Disabled providers stay hidden from runtime provider selection until enabled and saved.");
        return $"[{tone}]{icon} {SR.T("{0} in CodeAlta", stateText)}[/] [dim]· {AnsiMarkup.Escape(guidance)}[/]";
    }

    private static Visual CreateSectionRule(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return new Markup($"[dim]──── {AnsiMarkup.Escape(SR.T(label))} ────[/]")
        {
            Wrap = false,
        };
    }

    private bool TryBuildDefinitions(
        out IReadOnlyList<CodeAltaProviderDocument> definitions,
        out string errorMessage)
    {
        var result = new List<CodeAltaProviderDocument>(_providers.Count);
        foreach (var item in _providers)
        {
            if (!TryBuildDefinition(item, out var definition, out errorMessage))
            {
                definitions = [];
                return false;
            }

            result.Add(definition);
        }

        definitions = result;
        errorMessage = string.Empty;
        return true;
    }

    private bool TryBuildDefinition(
        ModelProviderEditorItemViewModel item,
        out CodeAltaProviderDocument definition,
        out string errorMessage)
    {
        var diagnostics = Analyze(item);
        var errors = diagnostics.Entries
            .Where(static entry => entry.Severity == ValidationSeverity.Error && IsSaveBlockingDiagnostic(entry))
            .Select(static entry => entry.Message)
            .ToArray();
        if (errors.Length > 0)
        {
            definition = null!;
            errorMessage = $"{item.Label}: {errors[0]}";
            return false;
        }

        definition = item.ToDocument();
        errorMessage = string.Empty;
        return true;
    }

    private static bool IsSaveBlockingDiagnostic(ModelProviderDiagnosticEntry entry)
        => !entry.Message.StartsWith("Last test failed:", StringComparison.Ordinal) &&
           !entry.Message.StartsWith(SR.T("Last test failed:"), StringComparison.Ordinal);

    private void SetSelectedProviderIndex(int index)
    {
        var normalizedIndex = _providers.Count == 0
            ? -1
            : Math.Clamp(index, 0, _providers.Count - 1);
        _selectedProviderIndex.Value = normalizedIndex;
    }

    private ModelProviderEditorItemViewModel CreateEditorItem(CodeAltaProviderDocument definition)
    {
        var item = ModelProviderEditorItemViewModel.FromDocument(definition);
        ApplyRuntimeStatus(item);
        return CreateEditorItem(item);
    }

    private ModelProviderEditorItemViewModel CreateEditorItem(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.SetEditedCallback(NotifyProviderDraftChanged);
        return item;
    }

    private void ApplyRuntimeStatus(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!TryGetRuntimeStatus(item, out var status))
        {
            return;
        }

        switch (status.Availability)
        {
            case ModelProviderAvailability.Ready:
                item.SetTestResult(success: true, FormatRuntimeStatusMessage(status));
                break;
            case ModelProviderAvailability.Failed:
            case ModelProviderAvailability.Unsupported:
                item.SetTestResult(success: false, status.StatusMessage);
                break;
            case ModelProviderAvailability.Probing:
                item.SetTestInProgress(status.StatusMessage);
                break;
        }
    }

    private bool TryGetRuntimeStatus(ModelProviderEditorItemViewModel item, out ProviderRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Enabled && !string.IsNullOrWhiteSpace(item.ProviderKey))
        {
            return _runtimeStatuses.TryGetValue(item.ProviderKey, out status);
        }

        status = default;
        return false;
    }

    private void MarkEnabledProvidersRefreshInProgress()
    {
        foreach (var item in _providers.Where(static item => item.Enabled))
        {
            item.SetTestInProgress(SR.T("Refreshing provider availability and model list..."));
        }
    }

    private void ClearProviderRefreshInProgress()
    {
        foreach (var item in _providers.Where(static item => item.LastTestState == ModelProviderLastTestState.Testing))
        {
            item.ClearTestResult();
        }
    }

    private string BuildStatusMarkup()
    {
        var statusText = _statusText.Value;
        if (_activeOperationCount.Value > 0)
        {
            return _activeOperationNoticeText.Value is { } noticeText
                ? $"{statusText}{Environment.NewLine}{noticeText}"
                : statusText;
        }

        if (!HasUnsavedChanges())
        {
            return statusText;
        }

        var unsavedStatus = $"[warning]{SR.T("Unsaved model provider changes. Save applies them; Refresh reloads from disk and retests saved providers.")}[/]";
        return IsPersistentStatusText(statusText)
            ? unsavedStatus
            : $"{statusText}{Environment.NewLine}{unsavedStatus}";
    }

    private string BuildBottomRightHintMarkup()
    {
        if (IsDialogOperationActive())
        {
            var hints = new List<string>();
            if (IsCancelableOperationActive())
            {
                hints.Add(SR.T("Ctrl+G Ctrl+C cancel"));
            }

            if (!string.IsNullOrWhiteSpace(_activeLoginUrl.Value))
            {
                hints.Add(SR.T("Ctrl+G Ctrl+U copy URL"));
            }

            if (!string.IsNullOrWhiteSpace(_activeLoginDeviceCode.Value))
            {
                hints.Add(SR.T("Ctrl+G Ctrl+D copy code"));
            }

            if (hints.Count > 0)
            {
                return $"[dim]{string.Join(" · ", hints)}[/]";
            }
        }

        return $"[dim]{SR.T("Ctrl+G Ctrl+R reopen · Esc close")}[/]";
    }

    private string BuildChangeSummaryMarkup()
    {
        if (_activeOperationCount.Value > 0)
        {
            return $"[primary]{SR.T("Operation in progress...")}[/]";
        }

        if (!_hasLoadedDefinitions.Value)
        {
            return $"[dim]{SR.T("Provider configuration not loaded yet.")}[/]";
        }

        return HasUnsavedChanges()
            ? $"[warning]● {SR.T("Unsaved changes")}[/] [dim]{SR.T("Save applies them; Refresh reloads from disk.")}[/]"
            : $"[success]✓ {SR.T("No unsaved changes")}[/] [dim]{SR.T("Configuration matches disk.")}[/]";
    }

    private static bool IsPersistentStatusText(string statusText)
        => statusText.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
           statusText.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
           statusText.Contains("configure model providers", StringComparison.OrdinalIgnoreCase);

    private static string FormatProviderSaveStatus(ProviderConfigurationSaveResult saveResult)
        => saveResult.RuntimeRefreshSucceeded
            ? $"[success]{SR.T("Provider configuration saved and runtime refreshed.")}[/]"
            : $"[warning]{SR.T("Provider configuration saved, but runtime refresh failed: {0}", AnsiMarkup.Escape(saveResult.RuntimeRefreshErrorMessage ?? SR.T("unknown error")))}[/]";

    private static void TryOpenBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // The Link still renders as an OSC 8 terminal hyperlink when shell launch is unavailable.
        }
    }

    private void QueueBackgroundOperation<TResult>(
        Func<TResult> work,
        Action<TResult> onCompleted,
        Action<Exception> onFailed)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onCompleted);
        ArgumentNullException.ThrowIfNull(onFailed);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = work();
                    await PublishBackgroundResultAsync(() => onCompleted(result));
                }
                catch (Exception ex)
                {
                    await PublishBackgroundResultAsync(() => onFailed(ex));
                }
            });
    }

    private void QueueBackgroundOperation<TResult>(
        Func<Task<TResult>> workAsync,
        Action<TResult> onCompleted,
        Action<Exception> onFailed)
    {
        ArgumentNullException.ThrowIfNull(workAsync);
        ArgumentNullException.ThrowIfNull(onCompleted);
        ArgumentNullException.ThrowIfNull(onFailed);

        QueueBackgroundOperation(_ => workAsync(), onCompleted, onFailed);
    }

    private void QueueBackgroundOperation<TResult>(
        Func<CancellationToken, Task<TResult>> workAsync,
        Action<TResult> onCompleted,
        Action<Exception> onFailed)
    {
        ArgumentNullException.ThrowIfNull(workAsync);
        ArgumentNullException.ThrowIfNull(onCompleted);
        ArgumentNullException.ThrowIfNull(onFailed);

        var cancellationToken = _activeOperationCancellation?.Token ?? CancellationToken.None;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await workAsync(cancellationToken);
                    await PublishBackgroundResultAsync(() => onCompleted(result));
                }
                catch (Exception ex)
                {
                    await PublishBackgroundResultAsync(() => onFailed(ex));
                }
            });
    }

    private Task PublishBackgroundResultAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return _dialog.Dispatcher.InvokeAsync(
            () =>
            {
                try
                {
                    action();
                }
                finally
                {
                    EndDialogOperation();
                }
            });
    }

    private void RequestClose()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(SR.T("close this dialog"));
            return;
        }

        if (!HasUnsavedChanges())
        {
            Close();
            return;
        }

        new ConfirmationDialog(
            SR.T("Discard Provider Changes?"),
            [SR.T("You have unsaved model provider changes."), SR.T("Close the dialog without saving?")],
            SR.T("Discard"),
            ControlTone.Error,
            () =>
            {
                Close();
                return Task.CompletedTask;
            },
            _getBounds,
            _getFocusTarget)
            .Show();
    }

    private void Close()
    {
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private ModelProviderEditorItemViewModel? GetSelectedItem()
        => _selectedProviderIndex.Value >= 0 && _selectedProviderIndex.Value < _providers.Count
            ? _providers[_selectedProviderIndex.Value]
            : null;

    private static Visual BuildEmptyState()
        => new TextBlock(SR.T("Add a provider on the left, or enable one of the reserved providers to get started."))
        {
            Wrap = true,
        };

    private ModelProviderDiagnosticsSnapshot Analyze(ModelProviderEditorItemViewModel item)
        => ModelProviderEditorDiagnostics.Analyze(item, _providers);

    private static (string Tone, string Icon) GetStatusToneAndIcon(ModelProviderUiStatusKind statusKind)
        => statusKind switch
        {
            ModelProviderUiStatusKind.Success => ("success", $"{TerminalIcons.MdCheckCircleOutline}"),
            ModelProviderUiStatusKind.Warning => ("warning", $"{TerminalIcons.MdAlertOutline}"),
            ModelProviderUiStatusKind.Error => ("error", $"{TerminalIcons.MdCloseCircleOutline}"),
            ModelProviderUiStatusKind.Disabled => ("muted", $"{TerminalIcons.MdPauseCircleOutline}"),
            _ => ("primary", $"{TerminalIcons.MdTuneVariant}"),
        };

    private static (string Tone, string Icon) GetAvailabilityToneAndIcon(bool enabled)
        => enabled
            ? ("success", $"{TerminalIcons.MdCheckCircleOutline}")
            : ("muted", $"{TerminalIcons.MdPauseCircleOutline}");

    private static (string Tone, string Icon, string Text) GetProviderListStatus(ModelProviderEditorItemViewModel item, ModelProviderDiagnosticsSnapshot diagnostics)
    {
        if (!item.Enabled)
        {
            return ("muted", $"{TerminalIcons.MdPauseCircleOutline}", SR.T("OFF"));
        }

        if (item.LastTestState == ModelProviderLastTestState.Testing)
        {
            return ("primary", $"{TerminalIcons.MdTimerOutline}", SR.T("TEST"));
        }

        return diagnostics.StatusKind switch
        {
            ModelProviderUiStatusKind.Success => ("success", $"{TerminalIcons.MdCheckCircleOutline}", SR.T("ON")),
            ModelProviderUiStatusKind.Error => ("error", $"{TerminalIcons.MdCloseCircleOutline}", SR.T("ERR")),
            ModelProviderUiStatusKind.Warning => ("warning", $"{TerminalIcons.MdAlertOutline}", SR.T("WARN")),
            _ => ("primary", $"{TerminalIcons.MdHelpBox}", SR.T("TEST")),
        };
    }

    private static string FormatRuntimeStatusMessage(ProviderRuntimeStatus status)
        => status.ModelCount switch
        {
            0 => SR.T("Runtime connected."),
            1 => SR.T("Runtime connected · 1 model discovered."),
            _ => SR.T("Runtime connected · {0} models discovered.", status.ModelCount),
        };

    private static ValidationMessage? BuildCustomApiUrlGuidance(ModelProviderEditorItemViewModel item)
        => item.Enabled &&
           !item.UseDefaultApiUrl &&
           !string.IsNullOrWhiteSpace(item.ApiUrl) &&
           ModelProviderEditorDiagnostics.ValidateApiUrl(item) is null
            ? new ValidationMessage(ValidationSeverity.Info, SR.T("Use Test Provider to verify this custom endpoint is reachable."))
            : null;

    private static string GetValidationMessageText(ValidationMessage message)
        => message.Content is TextBlock textBlock
            ? textBlock.Text ?? string.Empty
            : message.Content.ToString() ?? string.Empty;

    private void LoadDefinitionsIntoDialog(
        IReadOnlyList<CodeAltaProviderDocument> definitions,
        string emptyStatusText,
        string loadedStatusText)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentException.ThrowIfNullOrWhiteSpace(emptyStatusText);
        ArgumentException.ThrowIfNullOrWhiteSpace(loadedStatusText);

        var selectedProviderKey = GetSelectedItem()?.ProviderKey;

        _providers.Clear();
        _runtimeStatuses = _modelProviders.GetRuntimeStatuses();
        _providers.AddRange(definitions.Select(CreateEditorItem));
        _loadedSnapshot = _providers.Select(CreateSnapshot).ToArray();
        _hasLoadedDefinitions.Value = true;
        SetStatus(_providers.Count == 0 ? emptyStatusText : loadedStatusText);

        var selectedIndex = selectedProviderKey is null
            ? (_providers.Count == 0 ? -1 : 0)
            : FindProviderIndex(selectedProviderKey);
        if (selectedIndex < 0 && _providers.Count > 0)
        {
            selectedIndex = 0;
        }

        SetSelectedProviderIndex(selectedIndex);
        FocusProviderList();
    }

    private void FocusProviderList()
        => _dialog.App?.Focus(_providerList);

    private bool TryBeginDialogOperation(
        string operationDescription,
        ProviderDialogOperationKind operationKind = ProviderDialogOperationKind.None,
        bool canCancel = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDescription);

        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(operationDescription);
            return false;
        }

        _activeOperationCount.Value++;
        _activeOperationKind.Value = operationKind;
        _activeOperationNoticeText.Value = null;
        _activeLoginUrl.Value = null;
        _activeLoginDeviceCode.Value = null;
        _activeOperationCancellation = canCancel ? new CancellationTokenSource() : null;
        if (IsLoginOperation(operationKind))
        {
            ShowActiveLoginDialog(operationKind);
        }

        return true;
    }

    private void EndDialogOperation()
    {
        CloseActiveLoginDialog();
        _activeOperationCancellation?.Dispose();
        _activeOperationCancellation = null;
        _activeOperationKind.Value = ProviderDialogOperationKind.None;
        _activeOperationNoticeText.Value = null;
        _activeLoginUrl.Value = null;
        _activeLoginDeviceCode.Value = null;
        if (_activeOperationCount.Value > 0)
        {
            _activeOperationCount.Value--;
        }
    }

    private bool IsDialogOperationActive()
        => _activeOperationCount.Value > 0;

    private bool IsCancelableOperationActive()
        => _activeOperationCancellation is not null;

    private bool IsActiveOperation(ProviderDialogOperationKind operationKind)
        => operationKind != ProviderDialogOperationKind.None && _activeOperationKind.Value == operationKind;

    private void CancelActiveOperation()
    {
        var cancellation = _activeOperationCancellation;
        if (cancellation is null)
        {
            ReportActiveOperationBlock(SR.T("cancel the current operation"));
            return;
        }

        if (!cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }

        _activeOperationNoticeText.Value = $"[warning]{SR.T("Cancel requested. Waiting for the provider operation to stop...")}[/]";
        RefreshActiveLoginDialog();
    }

    private void ShowActiveLoginDialog(ProviderDialogOperationKind operationKind)
    {
        if (_dialog.App is null)
        {
            return;
        }

        if (_activeLoginDialog?.App is not null)
        {
            return;
        }

        var labels = GetLoginOperationLabels(operationKind);
        var cancelButton = new Button(labels.CancelLabel)
            .Tone(ControlTone.Warning)
            .Click(CancelActiveOperation);
        var content = new ComputedVisual(
            () =>
            {
                _ = _activeLoginDialogRefreshVersion.Value;
                return BuildActiveLoginDialogContent(labels);
            });

        var dialog = new Dialog()
            .Title(labels.Title)
            .TopRightText(cancelButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc cancel · Ctrl+G Ctrl+C cancel")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content)
            .Style(DialogStyle.Rounded);

        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Login.Cancel",
            LabelMarkup = labels.CancelLabel,
            DescriptionMarkup = SR.T("Cancel the active browser or device login."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => CancelActiveOperation(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Login.CancelSequence",
            LabelMarkup = labels.CancelLabel,
            DescriptionMarkup = SR.T("Cancel the active browser or device login."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl)),
            Importance = CommandImportance.Primary,
            Execute = _ => CancelActiveOperation(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Login.CopyUrl",
            LabelMarkup = SR.T("Copy Login URL"),
            DescriptionMarkup = SR.T("Copy the current login URL to the clipboard."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlU, TerminalModifiers.Ctrl)),
            Importance = CommandImportance.Secondary,
            CanExecute = _ => !string.IsNullOrWhiteSpace(_activeLoginUrl.Value),
            Execute = _ => CopyActiveLoginUrl(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Login.CopyDeviceCode",
            LabelMarkup = SR.T("Copy Device Code"),
            DescriptionMarkup = SR.T("Copy the current device login code to the clipboard."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlD, TerminalModifiers.Ctrl)),
            Importance = CommandImportance.Secondary,
            CanExecute = _ => !string.IsNullOrWhiteSpace(_activeLoginDeviceCode.Value),
            Execute = _ => CopyActiveLoginDeviceCode(),
        });

        _activeLoginDialog = dialog;
        ResponsiveDialogSize.Apply(dialog, _getBounds(), minWidth: 72, minHeight: 14, widthFactor: 0.56, heightFactor: 0.36);
        dialog.Show();
    }

    private Visual BuildActiveLoginDialogContent(LoginOperationLabels labels)
    {
        var instructions = new Markup($"[dim]{AnsiMarkup.Escape(labels.Instruction)} {SR.T("This dialog closes automatically when login completes.")}[/]")
        {
            Wrap = true,
        };

        var sections = new List<Visual>
        {
            instructions,
            CreateLoginUrlSection(),
        };

        if (!string.IsNullOrWhiteSpace(_activeLoginDeviceCode.Value))
        {
            sections.Add(CreateLoginDeviceCodeSection());
        }

        if (_activeOperationNoticeText.Value is { } noticeText)
        {
            sections.Add(new Markup(noticeText) { Wrap = true });
        }

        sections.Add(new HStack(
            new Button(SR.T("Copy URL"))
                .Tone(ControlTone.Default)
                .IsEnabled(() => !string.IsNullOrWhiteSpace(_activeLoginUrl.Value))
                .Click(CopyActiveLoginUrl),
            new Button(SR.T("Copy Code"))
                .Tone(ControlTone.Default)
                .IsEnabled(() => !string.IsNullOrWhiteSpace(_activeLoginDeviceCode.Value))
                .Click(CopyActiveLoginDeviceCode),
            new Button(labels.CancelLabel)
                .Tone(ControlTone.Warning)
                .Click(CancelActiveOperation))
        {
            HorizontalAlignment = Align.End,
            Spacing = 1,
        });

        return new VStack(sections.ToArray())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };
    }

    private Visual CreateLoginUrlSection()
    {
        if (string.IsNullOrWhiteSpace(_activeLoginUrl.Value))
        {
            return new VStack(
                new Markup($"[bold]{SR.T("URL")}[/]"),
                new Markup($"[dim]{SR.T("Waiting for the provider to return the login URL...")}[/]") { Wrap = true })
            {
                HorizontalAlignment = Align.Stretch,
                Spacing = 0,
            };
        }

        return new VStack(
            new Markup($"[bold]{SR.T("URL")}[/] [dim]{SR.T("Press Enter on any link line to open it.")}[/]"),
            CreateWrappedLoginLink(_activeLoginUrl.Value))
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 0,
        };
    }

    private Visual CreateLoginDeviceCodeSection()
        => new VStack(
            new Markup($"[bold]{SR.T("Code")}[/] [dim]{SR.T("Copy it, then paste it in the browser.")}[/]"),
            new HStack(
                new TextBox(() => _activeLoginDeviceCode.Value ?? string.Empty)
                    .IsEnabled(false)
                    .Stretch(),
                new Button(SR.T("Copy Code"))
                    .Tone(ControlTone.Primary)
                    .Click(CopyActiveLoginDeviceCode))
            {
                HorizontalAlignment = Align.Stretch,
                Spacing = 1,
            })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 0,
        };

    private static Visual CreateWrappedLoginLink(string uri)
    {
        var lines = SplitLoginUriForDisplay(uri).Select(segment =>
            (Visual)new Link(uri, segment)
                .Opened((_, e) =>
                {
                    TryOpenBrowser(e.Uri);
                    e.Handled = true;
                })
                .Tooltip(new TextBlock(SR.T("Open {0}", uri))));
        return new VStack(lines.ToArray())
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 0,
        };
    }

    private static IReadOnlyList<string> SplitLoginUriForDisplay(string uri)
    {
        const int maxSegmentLength = 64;
        if (uri.Length <= maxSegmentLength)
        {
            return [uri];
        }

        var segments = new List<string>();
        var start = 0;
        while (start < uri.Length)
        {
            var remaining = uri.Length - start;
            if (remaining <= maxSegmentLength)
            {
                segments.Add(uri[start..]);
                break;
            }

            var length = FindLoginUriWrapLength(uri, start, maxSegmentLength);
            segments.Add(uri.Substring(start, length));
            start += length;
        }

        return segments;
    }

    private static int FindLoginUriWrapLength(string uri, int start, int maxLength)
    {
        var endExclusive = Math.Min(uri.Length, start + maxLength);
        for (var index = endExclusive - 1; index > start + 16; index--)
        {
            if (uri[index] is '/' or '?' or '&' or '=' or '-' or '_' or '.')
            {
                return index - start + 1;
            }
        }

        return endExclusive - start;
    }

    private void CloseActiveLoginDialog()
    {
        var dialog = _activeLoginDialog;
        if (dialog is null)
        {
            return;
        }

        var app = dialog.App ?? _dialog.App;
        dialog.Close();
        _activeLoginDialog = null;
        app?.Focus(_dialog);
    }

    private void RefreshActiveLoginDialog()
    {
        if (_activeLoginDialog is not null)
        {
            _activeLoginDialogRefreshVersion.Value++;
        }
    }

    private static bool IsLoginOperation(ProviderDialogOperationKind operationKind)
        => operationKind is ProviderDialogOperationKind.CodexBrowserLogin
            or ProviderDialogOperationKind.CodexDeviceLogin
            or ProviderDialogOperationKind.CopilotBrowserLogin
            or ProviderDialogOperationKind.CopilotDeviceLogin
            or ProviderDialogOperationKind.XaiBrowserLogin
            or ProviderDialogOperationKind.XaiDeviceLogin;

    private static LoginOperationLabels GetLoginOperationLabels(ProviderDialogOperationKind operationKind)
        => operationKind switch
        {
            ProviderDialogOperationKind.CodexBrowserLogin => new LoginOperationLabels(
                SR.T("ChatGPT Browser Login"),
                SR.T("Cancel Browser Login"),
                SR.T("Complete ChatGPT browser login in your browser, then return to CodeAlta.")),
            ProviderDialogOperationKind.CodexDeviceLogin => new LoginOperationLabels(
                SR.T("ChatGPT Device Login"),
                SR.T("Cancel Device Login"),
                SR.T("Open the ChatGPT verification URL and enter the device code shown below.")),
            ProviderDialogOperationKind.CopilotBrowserLogin => new LoginOperationLabels(
                SR.T("Copilot Browser Login"),
                SR.T("Cancel Browser Login"),
                SR.T("Complete Copilot browser login in your browser, then return to CodeAlta.")),
            ProviderDialogOperationKind.CopilotDeviceLogin => new LoginOperationLabels(
                SR.T("Copilot Device Login"),
                SR.T("Cancel Device Login"),
                SR.T("Open the Copilot verification URL and enter the device code shown below.")),
            ProviderDialogOperationKind.XaiBrowserLogin => new LoginOperationLabels(
                SR.T("xAI Grok Browser Login"),
                SR.T("Cancel Browser Login"),
                SR.T("Complete xAI Grok browser login in your browser, then return to CodeAlta.")),
            ProviderDialogOperationKind.XaiDeviceLogin => new LoginOperationLabels(
                SR.T("xAI Grok Device Login"),
                SR.T("Cancel Device Login"),
                SR.T("Open the xAI verification URL and enter the device code shown below.")),
            _ => new LoginOperationLabels(
                SR.T("Provider Login"),
                SR.T("Cancel Login"),
                SR.T("Complete the provider login in your browser, then return to CodeAlta.")),
        };

    private void CopyActiveLoginUrl()
        => CopyActiveLoginValue(_activeLoginUrl.Value, SR.T("login URL"));

    private void CopyActiveLoginDeviceCode()
        => CopyActiveLoginValue(_activeLoginDeviceCode.Value, SR.T("device code"));

    private void CopyActiveLoginValue(string? value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (string.IsNullOrWhiteSpace(value))
        {
            SetProviderOperationNotice($"[warning]{SR.T("No {0} is available to copy yet.", AnsiMarkup.Escape(label))}[/]");
            return;
        }

        if (_dialog.App?.Terminal.Clipboard.TrySetText(value) == true)
        {
            SetProviderOperationNotice($"[success]{SR.T("Copied {0} to clipboard.", AnsiMarkup.Escape(label))}[/]");
            return;
        }

        SetProviderOperationNotice($"[warning]{SR.T("Could not copy {0} to the clipboard.", AnsiMarkup.Escape(label))}[/]");
    }

    private void CaptureActiveLoginDetails(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (TryFindFirstAbsoluteHttpUri(message, out var uri))
        {
            _activeLoginUrl.Value = uri;
        }

        if (TryFindDeviceCode(message, out var deviceCode))
        {
            _activeLoginDeviceCode.Value = deviceCode;
        }

        RefreshActiveLoginDialog();
    }

    private static bool TryFindFirstAbsoluteHttpUri(string text, out string uri)
    {
        var start = text.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        var httpsStart = text.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (httpsStart >= 0 && (start < 0 || httpsStart < start))
        {
            start = httpsStart;
        }

        if (start < 0)
        {
            uri = string.Empty;
            return false;
        }

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var candidate = text[start..end].TrimEnd('.', ',', ';', ':', ')', ']');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed.ToString();
            return true;
        }

        uri = string.Empty;
        return false;
    }

    private static bool TryFindDeviceCode(string text, out string deviceCode)
    {
        const string marker = "code";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (markerIndex >= 0)
        {
            var cursor = markerIndex + marker.Length;
            if (cursor < text.Length && !char.IsWhiteSpace(text[cursor]))
            {
                markerIndex = text.IndexOf(marker, cursor, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            var start = cursor;
            while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not '.' and not ',' and not ';')
            {
                cursor++;
            }

            if (cursor > start)
            {
                deviceCode = text[start..cursor].Trim();
                return true;
            }

            markerIndex = text.IndexOf(marker, cursor, StringComparison.OrdinalIgnoreCase);
        }

        deviceCode = string.Empty;
        return false;
    }

    private void SetProviderOperationNotice(string markup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markup);
        if (IsDialogOperationActive())
        {
            _activeOperationNoticeText.Value = markup;
            RefreshActiveLoginDialog();
            return;
        }

        SetStatus(markup);
    }

    private void ReportActiveOperationBlock(string operationDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDescription);
        var nextStep = IsCancelableOperationActive() ? SR.T("Cancel it or wait") : SR.T("Wait for it to finish");
        _activeOperationNoticeText.Value = $"[warning]{SR.T("Current provider operation is still running. {0} before trying to {1}.", nextStep, AnsiMarkup.Escape(operationDescription))}[/]";
        RefreshActiveLoginDialog();
    }

    private void SetStatus(string statusText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        _statusText.Value = statusText;
    }

    private static ModelProviderEditorItemViewModel.IBindings GetBindings(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (ModelProviderEditorItemViewModel.IBindings)item;
    }

    private readonly record struct ProviderTypeOption(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private readonly record struct ReasoningOption(string? Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private readonly record struct ProviderDefinitionsSaveDialogResult(
        IReadOnlyList<CodeAltaProviderDocument> DefinitionsFromDisk,
        ProviderConfigurationSaveResult SaveResult);

    private readonly record struct LoginOperationLabels(
        string Title,
        string CancelLabel,
        string Instruction);

    private enum ProviderDialogOperationKind
    {
        None,
        CodexBrowserLogin,
        CodexDeviceLogin,
        CopilotBrowserLogin,
        CopilotDeviceLogin,
        XaiBrowserLogin,
        XaiDeviceLogin,
    }


    private Visual BuildProviderListItem(DataTemplateValue<ModelProviderEditorItemViewModel> value)
    {
        return new Markup(() => BuildProviderListItemMarkup(value.GetValue()))
        {
            Wrap = false,
        };
    }

    private string BuildProviderListItemMarkup(ModelProviderEditorItemViewModel item)
    {
        var diagnostics = Analyze(item);
        var (tone, icon) = GetStatusToneAndIcon(diagnostics.StatusKind);
        var (availabilityTone, availabilityIcon, availabilityText) = GetProviderListStatus(item, diagnostics);
        return $"[{availabilityTone}]{availabilityIcon} {availabilityText}[/] [{tone}]{icon} {AnsiMarkup.Escape(item.Label)}[/] [dim]· {AnsiMarkup.Escape(diagnostics.StatusText)}[/]";
    }

    private bool HasUnsavedChanges()
    {
        _ = _providerEditVersion.Value;

        if (_providers.Count != _loadedSnapshot.Count)
        {
            return true;
        }

        for (var index = 0; index < _providers.Count; index++)
        {
            if (!CreateSnapshot(_providers[index]).Equals(_loadedSnapshot[index]))
            {
                return true;
            }
        }

        return false;
    }

    private void NotifyProviderDraftChanged()
        => _providerEditVersion.Value++;

    private int FindProviderIndex(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        for (var index = 0; index < _providers.Count; index++)
        {
            if (string.Equals(_providers[index].ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static ProviderDraftSnapshot CreateSnapshot(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return CreateSnapshot(item.ToDocument());
    }

    private static ProviderDraftSnapshot CreateSnapshot(CodeAltaProviderDocument definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new ProviderDraftSnapshot(
            definition.ProviderKey,
            definition.Enabled,
            definition.DisplayName,
            definition.ProviderType,
            definition.Model,
            definition.ReasoningEffort,
            definition.ApiKey,
            definition.ApiKeyEnv,
            definition.ApiUrl,
            definition.OrganizationId,
            definition.ProjectId,
            definition.Project,
            definition.Location,
            definition.ModelsDevProviderId,
            definition.SingleModelId,
            definition.AuthSource,
            definition.AccountId,
            definition.ModelDiscovery,
            definition.ResponseTransport,
            definition.Experimental);
    }

    private readonly record struct ProviderDraftSnapshot(
        string ProviderKey,
        bool? Enabled,
        string? DisplayName,
        string? ProviderType,
        string? Model,
        string? ReasoningEffort,
        string? ApiKey,
        string? ApiKeyEnv,
        string? ApiUrl,
        string? OrganizationId,
        string? ProjectId,
        string? Project,
        string? Location,
        string? ModelsDevProviderId,
        string? SingleModelId,
        string? AuthSource,
        string? AccountId,
        string? ModelDiscovery,
        string? ResponseTransport,
        bool? Experimental);
}
