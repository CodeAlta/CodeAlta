using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
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
    private static readonly ProviderTypeOption[] ProviderTypes =
    [
        new("openai-chat", "OpenAI Chat"),
        new("openai-responses", "OpenAI Responses"),
        new("codex", "Codex"),
        new("copilot", "Copilot"),
        new("anthropic", "Anthropic"),
        new("google-genai", "Google GenAI"),
        new("vertex-ai", "Vertex AI"),
    ];

    private static readonly ReasoningOption[] ReasoningOptions =
    [
        new("none", "None"),
        new("minimal", "Minimal"),
        new("low", "Low"),
        new("medium", "Medium"),
        new("high", "High"),
        new("xhigh", "XHigh"),
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
    private readonly State<bool> _hasLoadedDefinitions = new(false);
    private readonly State<string> _statusText = new("[dim]Configure model providers and save to refresh the runtime.[/]");
    private CancellationTokenSource? _activeOperationCancellation;

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

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
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

        var addButton = new Button($"{NerdFont.MdPlus} Add")
            .Tone(ControlTone.Primary)
            .Click(AddProvider);
        var deleteButton = new Button($"{NerdFont.MdDelete} Delete")
            .Tone(ControlTone.Error)
            .Click(DeleteSelectedProvider);
        var reloadButton = new Button("Reload")
            .Tone(ControlTone.Warning)
            .Click(() => StartReload(confirmWhenDirty: true));
        var advancedButton = new Button($"{NerdFont.MdCodeBraces} Advanced TOML")
            .Tone(ControlTone.Default)
            .Click(OpenAdvancedEditor);
        _saveButton = new Button("Save")
            .Tone(ControlTone.Success)
            .IsEnabled(() => _activeOperationCount.Value == 0 && HasUnsavedChanges())
            .Click(StartSave);

        var toolbar = new HStack(addButton, deleteButton, reloadButton, advancedButton, _saveButton)
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
            new Group("Providers")
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
            new Group("Provider Details")
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

        var intro = new Markup("[dim]Enable the providers you want available in CodeAlta. Warnings explain why a provider may not start, Test Provider verifies current settings before you save, and Advanced TOML opens the full config editor for provider settings not exposed here.[/]")
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
            .Title("Model Providers")
            .TopRightText(closeButton)
            .BottomRightText(new Markup(BuildBottomRightHintMarkup))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 110, minHeight: 28, widthFactor: 0.80, heightFactor: 0.80);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Manage.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the model providers dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => RequestClose(),
        });
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Providers.Manage.CancelOperation",
            LabelMarkup = "Cancel Provider Operation",
            DescriptionMarkup = "Cancel the current cancelable provider operation.",
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
            LabelMarkup = "Copy Login URL",
            DescriptionMarkup = "Copy the current login URL to the clipboard.",
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
            LabelMarkup = "Copy Device Code",
            DescriptionMarkup = "Copy the current device login code to the clipboard.",
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
        if (!_hasLoadedDefinitions.Value)
        {
            StartReload(confirmWhenDirty: false);
        }
    }

    private void StartReload(bool confirmWhenDirty)
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock("reload provider configuration");
            return;
        }

        if (confirmWhenDirty && HasUnsavedChanges())
        {
            new ConfirmationDialog(
                "Reload Provider Configuration?",
                ["Discard unsaved provider changes and reload from disk?"],
                "Reload",
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

        if (!TryBeginDialogOperation("reload provider configuration"))
        {
            return;
        }

        try
        {
            SetStatus("[primary]Loading provider configuration...[/]");
            QueueBackgroundOperation(
                _modelProviders.LoadDefinitions,
                definitions => LoadDefinitionsIntoDialog(
                    definitions,
                    emptyStatusText: "[warning]No providers are configured yet. Add one, or enable Codex/Copilot.[/]",
                    loadedStatusText: "[dim]Provider configuration loaded from disk.[/]"),
                ex => SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]"));
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
            ReportActiveOperationBlock("add a provider");
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
        SetStatus("[warning]Added provider. Configure required fields, then Save to apply.[/]");
    }

    private void DeleteSelectedProvider()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock("delete a provider");
            return;
        }

        var item = GetSelectedItem();
        if (item is null)
        {
            SetStatus("[warning]Select a provider to delete.[/]");
            return;
        }

        if (item.IsReserved)
        {
            SetStatus("[warning]Reserved providers cannot be deleted. Disable them instead.[/]");
            return;
        }

        _providers.Remove(item);
        SetSelectedProviderIndex(Math.Clamp(_selectedProviderIndex.Value, 0, _providers.Count - 1));
        NotifyProviderDraftChanged();
        SetStatus("[warning]Removed provider. Save to apply, or Reload to discard.[/]");
    }

    private void StartSave()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock("save provider changes");
            return;
        }

        if (!TryBuildDefinitions(out var definitions, out var errorMessage))
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(errorMessage)}[/]");
            return;
        }

        if (!HasUnsavedChanges())
        {
            SetStatus("[dim]No provider changes to save.[/]");
            return;
        }

        if (!TryBeginDialogOperation("save provider changes"))
        {
            return;
        }

        SetStatus("[primary]Saving provider configuration...[/]");
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
                    emptyStatusText: "[warning]No providers are configured yet. Add one, or enable Codex/Copilot.[/]",
                    loadedStatusText: FormatProviderSaveStatus(result.SaveResult));
            },
            ex => SetStatus($"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]"));
    }

    private void OpenAdvancedEditor()
    {
        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock("open Advanced TOML");
            return;
        }

        if (HasUnsavedChanges())
        {
            SetStatus("[warning]Save or Reload the form changes before opening Advanced TOML so the code editor starts from the on-disk config.[/]");
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
                emptyStatusText: "[warning]Provider configuration saved, but no providers are configured yet. Add one, or enable Codex/Copilot.[/]",
                loadedStatusText: FormatProviderSaveStatus(saveResult));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"[error]Saved TOML, but failed to reload provider configuration: {AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]");
        }
    }

    private void StartTest(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock("test a provider");
            return;
        }

        if (!_providers.Contains(item))
        {
            SetStatus("[warning]Select a provider to test.[/]");
            return;
        }

        if (!TryBuildDefinition(item, out var definition, out var errorMessage))
        {
            SetStatus($"[warning]{AnsiMarkup.Escape(errorMessage)}[/]");
            return;
        }

        if (!TryBeginDialogOperation("test a provider"))
        {
            return;
        }

        SetStatus($"[primary]Testing {AnsiMarkup.Escape(item.Label)}...[/]");
        QueueBackgroundOperation(
            cancellationToken => _modelProviders.TestProviderAsync(definition, cancellationToken),
            result =>
            {
                if (_providers.Contains(item))
                {
                    item.SetTestResult(result.Success, result.Message);
                }

                SetStatus(result.Success
                    ? $"[success]{AnsiMarkup.Escape(result.Message)}[/]"
                    : $"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
            },
            ex =>
            {
                if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)
                {
                    SetStatus("[warning]Provider test canceled.[/]");
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

        if (IsDialogOperationActive())
        {
            ReportActiveOperationBlock(operationDescription);
            return;
        }

        if (!_providers.Contains(item))
        {
            SetStatus($"[warning]Select a provider to {AnsiMarkup.Escape(operationDescription)}.[/]");
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
                    item.SetTestResult(result.Success, result.Message);
                }

                SetStatus(result.Success
                    ? $"[success]{AnsiMarkup.Escape(result.Message)}[/]"
                    : $"[warning]{AnsiMarkup.Escape(result.Message)}[/]");
            },
            ex =>
            {
                if (ex is OperationCanceledException || ex.GetBaseException() is OperationCanceledException)
                {
                    SetStatus("[warning]Provider operation canceled.[/]");
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

        var testButton = new Button("Test Provider")
            .Tone(ControlTone.Primary)
            .Click(() => StartTest(item));
        var codexActions = item.ProviderType == "codex"
            ? CreateCodexSubscriptionActions(item)
            : null;
        var copilotDirectActions = item.ProviderType == "copilot"
            ? CreateCopilotDirectActions(item)
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
        AddTextRow(form, ref row, "Provider Key", CreateKeyField(item), CreateSpacer());
        AddSelectRow(form, ref row, "Type", CreateTypeSelect(item), CreateSpacer());
        AddCheckRow(form, ref row, "Enabled", CreateEnabledCheckBox(item), CreateSpacer());
        AddTextRow(form, ref row, "Display Name", CreateDefaultTextField(bindings.DisplayName, () => item.UseDefaultDisplayName), CreateDefaultCheckBox("Default", bindings.UseDefaultDisplayName));
        AddTextRow(form, ref row, "Model", CreateDefaultTextField(bindings.Model, () => item.UseDefaultModel), CreateDefaultCheckBox("Default", bindings.UseDefaultModel));
        AddSelectRow(form, ref row, "Reasoning", CreateReasoningSelect(item), CreateDefaultCheckBox("Default", bindings.UseDefaultReasoningEffort));

        if (item.ProviderType is "openai-chat" or "openai-responses" or "anthropic" or "google-genai")
        {
            AddTextRow(form, ref row, "API Key", CreateApiKeyBox(item), CreateDefaultCheckBox("Default", bindings.UseDefaultApiKey));
            AddTextRow(form, ref row, "API Key Env", CreateApiKeyEnvField(item), CreateDefaultCheckBox("Default", bindings.UseDefaultApiKeyEnv));
        }

        if (item.ProviderType is "openai-chat" or "openai-responses" or "codex" or "copilot" or "anthropic" or "google-genai" or "vertex-ai")
        {
            AddTextRow(form, ref row, "API URL", CreateApiUrlField(item), CreateDefaultCheckBox("Default", bindings.UseDefaultApiUrl));
        }

        if (item.ProviderType is "openai-chat" or "openai-responses")
        {
            AddTextRow(form, ref row, "Organization", CreateDefaultTextField(bindings.OrganizationId, () => item.UseDefaultOrganizationId), CreateDefaultCheckBox("Default", bindings.UseDefaultOrganizationId));
            AddTextRow(form, ref row, "Project Id", CreateDefaultTextField(bindings.ProjectId, () => item.UseDefaultProjectId), CreateDefaultCheckBox("Default", bindings.UseDefaultProjectId));
        }

        if (item.ProviderType == "vertex-ai")
        {
            AddTextRow(form, ref row, "Project", CreateVertexProjectField(item), CreateDefaultCheckBox("Default", bindings.UseDefaultProject));
            AddTextRow(form, ref row, "Location", CreateVertexLocationField(item), CreateDefaultCheckBox("Default", bindings.UseDefaultLocation));
        }

        if (item.ProviderType is "openai-chat" or "openai-responses" or "codex" or "copilot" or "anthropic" or "google-genai" or "vertex-ai")
        {
            AddTextRow(form, ref row, "Models.dev Id", CreateDefaultTextField(bindings.ModelsDevProviderId, () => item.UseDefaultModelsDevProviderId), CreateDefaultCheckBox("Default", bindings.UseDefaultModelsDevProviderId));
            AddTextRow(form, ref row, "Single Model Id", CreateDefaultTextField(bindings.SingleModelId, () => item.UseDefaultSingleModelId), CreateDefaultCheckBox("Default", bindings.UseDefaultSingleModelId));
        }

        if (item.ProviderType == "codex")
        {
            AddTextRow(form, ref row, "Auth Source", CreateDefaultTextField(bindings.AuthSource, () => item.UseDefaultAuthSource), CreateDefaultCheckBox("Default", bindings.UseDefaultAuthSource));
            AddTextRow(form, ref row, "Account/Workspace Id", CreateDefaultTextField(bindings.AccountId, () => item.UseDefaultAccountId), CreateDefaultCheckBox("Default", bindings.UseDefaultAccountId));
            AddTextRow(form, ref row, "Model Discovery", CreateDefaultTextField(bindings.ModelDiscovery, () => item.UseDefaultModelDiscovery), CreateDefaultCheckBox("Default", bindings.UseDefaultModelDiscovery));
            AddTextRow(form, ref row, "Response Transport", CreateDefaultTextField(bindings.ResponseTransport, () => item.UseDefaultResponseTransport), CreateDefaultCheckBox("Default", bindings.UseDefaultResponseTransport));
        }
        else if (item.ProviderType == "copilot")
        {
            AddTextRow(form, ref row, "Auth Source", CreateDefaultTextField(bindings.AuthSource, () => item.UseDefaultAuthSource), CreateDefaultCheckBox("Default", bindings.UseDefaultAuthSource));
            AddTextRow(form, ref row, "GitHub Enterprise", CreateDefaultTextField(bindings.GitHubEnterpriseUrl, () => item.UseDefaultGitHubEnterpriseUrl), CreateDefaultCheckBox("Default", bindings.UseDefaultGitHubEnterpriseUrl));
            AddTextRow(form, ref row, "Model Discovery", CreateDefaultTextField(bindings.ModelDiscovery, () => item.UseDefaultModelDiscovery), CreateDefaultCheckBox("Default", bindings.UseDefaultModelDiscovery));
        }

        var advancedNotice = new Markup("[dim]Advanced provider TOML sections such as profile, compaction, extra_body, and model_overrides are preserved when you save from this form. Use Advanced TOML to edit them directly.[/]")
        {
            Wrap = true,
        };

        var detailContent = new List<Visual>
        {
            title,
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
            new Markup("[dim]ChatGPT/Codex subscription actions never send a model turn. Use Account/Workspace Id to pin a specific account when required.[/]") { Wrap = true },
            new HStack(
                CreateCancelableProviderActionButton(
                    item,
                    "Browser Login",
                    "Cancel Browser Login",
                    ProviderDialogOperationKind.CodexBrowserLogin,
                    "start ChatGPT browser login",
                    "Starting ChatGPT browser login...",
                    _modelProviders.LoginWithBrowserAsync),
                CreateCancelableProviderActionButton(
                    item,
                    "Device Login",
                    "Cancel Device Login",
                    ProviderDialogOperationKind.CodexDeviceLogin,
                    "start ChatGPT device-code login",
                    "Requesting ChatGPT device code...",
                    _modelProviders.LoginWithDeviceCodeAsync),
                new Button("Test Auth")
                    .Tone(ControlTone.Primary)
                    .Click(() => StartProviderAction(
                        item,
                        "test ChatGPT authentication",
                        "Testing ChatGPT authentication without sending a model turn...",
                        definition => _modelProviders.TestAuthenticationAsync(definition))),
                new Button("List Models")
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        "list Codex subscription models",
                        "Listing Codex subscription models without sending a model turn...",
                        definition => _modelProviders.ListModelsAsync(definition))),
                new Button("List Accounts")
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        "list ChatGPT accounts/workspaces",
                        "Reading ChatGPT account/workspace metadata...",
                        definition => _modelProviders.ListAccountsAsync(definition))),
                new Button("Logout")
                    .Tone(ControlTone.Error)
                    .Click(() => StartProviderAction(
                        item,
                        "logout ChatGPT credentials",
                        "Deleting CodeAlta-owned ChatGPT/Codex credentials...",
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
            new Markup("[dim]Copilot login opens GitHub's device authorization page, then stores CodeAlta-owned GitHub/Copilot tokens for this provider. No model turn is sent.[/]") { Wrap = true },
            new HStack(
                CreateCancelableProviderActionButton(
                    item,
                    "Browser Login",
                    "Cancel Browser Login",
                    ProviderDialogOperationKind.CopilotBrowserLogin,
                    "start Copilot browser login",
                    "Requesting Copilot login code...",
                    _modelProviders.LoginWithBrowserAsync),
                CreateCancelableProviderActionButton(
                    item,
                    "Device Login",
                    "Cancel Device Login",
                    ProviderDialogOperationKind.CopilotDeviceLogin,
                    "start Copilot device-code login",
                    "Requesting Copilot device code...",
                    _modelProviders.LoginWithDeviceCodeAsync),
                new Button("Test Auth")
                    .Tone(ControlTone.Primary)
                    .Click(() => StartProviderAction(
                        item,
                        "test Copilot authentication",
                        "Checking cached Copilot credentials...",
                        definition => _modelProviders.TestAuthenticationAsync(definition))),
                new Button("List Models")
                    .Tone(ControlTone.Default)
                    .Click(() => StartProviderAction(
                        item,
                        "list Copilot models",
                        "Listing Copilot models without sending a model turn...",
                        definition => _modelProviders.ListModelsAsync(definition))),
                new Button("Logout")
                    .Tone(ControlTone.Error)
                    .Click(() => StartProviderAction(
                        item,
                        "logout Copilot credentials",
                        "Deleting CodeAlta-owned Copilot credentials...",
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
        => new CheckBox("Enabled").IsChecked(GetBindings(item).Enabled);

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
        => new CheckBox(label).IsChecked(binding);

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
        form.Cell(new TextBlock(label) { VerticalAlignment = Align.Center }, row, 0);
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
                ? "[primary]Configured.[/] Use [bold]Test Provider[/] to confirm this provider works before saving."
                : "This provider is disabled. Enable it when you are ready to use it.";
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
                        ValidationSeverity.Error => $"{NerdFont.MdCloseCircleOutline}",
                        ValidationSeverity.Warning => $"{NerdFont.MdAlertOutline}",
                        _ => $"{NerdFont.MdInformationOutline}",
                    };
                    return $"[{tone}]{icon} {AnsiMarkup.Escape(entry.Message)}[/]";
                }));
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
        => !entry.Message.StartsWith("Last test failed:", StringComparison.Ordinal);

    private void SetSelectedProviderIndex(int index)
    {
        var normalizedIndex = _providers.Count == 0
            ? -1
            : Math.Clamp(index, 0, _providers.Count - 1);
        _selectedProviderIndex.Value = normalizedIndex;
    }

    private ModelProviderEditorItemViewModel CreateEditorItem(CodeAltaProviderDocument definition)
        => CreateEditorItem(ModelProviderEditorItemViewModel.FromDocument(definition));

    private ModelProviderEditorItemViewModel CreateEditorItem(ModelProviderEditorItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.SetEditedCallback(NotifyProviderDraftChanged);
        return item;
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

        const string unsavedStatus = "[warning]Unsaved model provider changes. Save to refresh the runtime, or Reload to discard them.[/]";
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
                hints.Add("Ctrl+G Ctrl+C cancel");
            }

            if (!string.IsNullOrWhiteSpace(_activeLoginUrl.Value))
            {
                hints.Add("Ctrl+G Ctrl+U copy URL");
            }

            if (!string.IsNullOrWhiteSpace(_activeLoginDeviceCode.Value))
            {
                hints.Add("Ctrl+G Ctrl+D copy code");
            }

            if (hints.Count > 0)
            {
                return $"[dim]{string.Join(" · ", hints)}[/]";
            }
        }

        return "[dim]Ctrl+G Ctrl+R reopen · Esc close[/]";
    }

    private string BuildChangeSummaryMarkup()
    {
        if (_activeOperationCount.Value > 0)
        {
            return "[primary]Operation in progress...[/]";
        }

        if (!_hasLoadedDefinitions.Value)
        {
            return "[dim]Provider configuration not loaded yet.[/]";
        }

        return HasUnsavedChanges()
            ? "[warning]● Unsaved changes[/] [dim]Save applies them; Reload discards them.[/]"
            : "[success]✓ No unsaved changes[/] [dim]Configuration matches disk.[/]";
    }

    private static bool IsPersistentStatusText(string statusText)
        => statusText.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
           statusText.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
           statusText.Contains("configure model providers", StringComparison.OrdinalIgnoreCase);

    private static string FormatProviderSaveStatus(ProviderConfigurationSaveResult saveResult)
        => saveResult.RuntimeRefreshSucceeded
            ? "[success]Provider configuration saved and runtime refreshed.[/]"
            : $"[warning]Provider configuration saved, but runtime refresh failed: {AnsiMarkup.Escape(saveResult.RuntimeRefreshErrorMessage ?? "unknown error")}[/]";

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
            ReportActiveOperationBlock("close this dialog");
            return;
        }

        if (!HasUnsavedChanges())
        {
            Close();
            return;
        }

        new ConfirmationDialog(
            "Discard Provider Changes?",
            ["You have unsaved model provider changes.", "Close the dialog without saving?"],
            "Discard",
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
        => new TextBlock("Add a provider on the left, or enable one of the reserved providers to get started.")
        {
            Wrap = true,
        };

    private ModelProviderDiagnosticsSnapshot Analyze(ModelProviderEditorItemViewModel item)
        => ModelProviderEditorDiagnostics.Analyze(item, _providers);

    private static (string Tone, string Icon) GetStatusToneAndIcon(ModelProviderUiStatusKind statusKind)
        => statusKind switch
        {
            ModelProviderUiStatusKind.Success => ("success", $"{NerdFont.MdCheckCircleOutline}"),
            ModelProviderUiStatusKind.Warning => ("warning", $"{NerdFont.MdAlertOutline}"),
            ModelProviderUiStatusKind.Error => ("error", $"{NerdFont.MdCloseCircleOutline}"),
            ModelProviderUiStatusKind.Disabled => ("muted", $"{NerdFont.MdPauseCircleOutline}"),
            _ => ("primary", $"{NerdFont.MdTuneVariant}"),
        };

    private static ValidationMessage? BuildCustomApiUrlGuidance(ModelProviderEditorItemViewModel item)
        => item.Enabled &&
           !item.UseDefaultApiUrl &&
           !string.IsNullOrWhiteSpace(item.ApiUrl) &&
           ModelProviderEditorDiagnostics.ValidateApiUrl(item) is null
            ? new ValidationMessage(ValidationSeverity.Info, "Use Test Provider to verify this custom endpoint is reachable.")
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
    }

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
        return true;
    }

    private void EndDialogOperation()
    {
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
            ReportActiveOperationBlock("cancel the current operation");
            return;
        }

        if (!cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }

        _activeOperationNoticeText.Value = "[warning]Cancel requested. Waiting for the provider operation to stop...[/]";
    }

    private void CopyActiveLoginUrl()
        => CopyActiveLoginValue(_activeLoginUrl.Value, "login URL");

    private void CopyActiveLoginDeviceCode()
        => CopyActiveLoginValue(_activeLoginDeviceCode.Value, "device code");

    private void CopyActiveLoginValue(string? value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (string.IsNullOrWhiteSpace(value))
        {
            SetProviderOperationNotice($"[warning]No {AnsiMarkup.Escape(label)} is available to copy yet.[/]");
            return;
        }

        if (_dialog.App?.Terminal.Clipboard.TrySetText(value) == true)
        {
            SetProviderOperationNotice($"[success]Copied {AnsiMarkup.Escape(label)} to clipboard.[/]");
            return;
        }

        SetProviderOperationNotice($"[warning]Could not copy {AnsiMarkup.Escape(label)} to the clipboard.[/]");
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
            return;
        }

        SetStatus(markup);
    }

    private void ReportActiveOperationBlock(string operationDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDescription);
        var nextStep = IsCancelableOperationActive() ? "Cancel it or wait" : "Wait for it to finish";
        _activeOperationNoticeText.Value = $"[warning]Current provider operation is still running. {nextStep} before trying to {AnsiMarkup.Escape(operationDescription)}.[/]";
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

    private enum ProviderDialogOperationKind
    {
        None,
        CodexBrowserLogin,
        CodexDeviceLogin,
        CopilotBrowserLogin,
        CopilotDeviceLogin,
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
        return $"[{tone}]{icon} {AnsiMarkup.Escape(item.Label)}[/] [dim]· {AnsiMarkup.Escape(diagnostics.StatusText)}[/]";
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
