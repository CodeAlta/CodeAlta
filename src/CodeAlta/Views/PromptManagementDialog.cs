using System.Text;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Presentation.Chat;
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
using XenoAtom.Terminal.UI.Templating;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class PromptManagementDialog
{
    private const string PromptEditorLanguageId = "markdown";
    private const string PromptEditorFileName = "prompt.md";

    private readonly CatalogOptions _catalogOptions;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action _onPromptsChanged;
    private readonly Action<string, StatusTone> _setStatus;
    private readonly AgentPromptCatalog _promptCatalog;
    private readonly OptionList<PromptRow> _promptList;
    private readonly OptionList<SystemPromptRow> _systemPromptList;
    private readonly TextBox _nameBox;
    private readonly TextBox _descriptionBox;
    private readonly TextBox _systemBox;
    private readonly CodeEditor _bodyEditor;
    private readonly State<int> _selectionVersion = new(0);
    private readonly State<int> _editVersion = new(0);
    private readonly State<string?> _statusMessage = new(null);
    private Dialog? _dialog;
    private IReadOnlyList<PromptRow> _rows = [];
    private IReadOnlyList<SystemPromptRow> _systemRows = [];
    private PromptRow? _selectedRow;
    private SystemPromptRow? _selectedSystemRow;
    private PromptResourceTab _activeTab = PromptResourceTab.AgentPrompt;
    private bool _loadingSelection;
    private bool _suppressTabSelectionChanged;
    private bool _suppressEditorChanged;
    private string _loadedName = string.Empty;
    private string? _loadedDescription;
    private string _loadedSystem = AgentPromptCatalog.DefaultPromptName;
    private string _loadedBody = string.Empty;

    public PromptManagementDialog(
        CatalogOptions catalogOptions,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget,
        Action onPromptsChanged,
        Action<string, StatusTone> setStatus,
        AgentPromptCatalog? promptCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(onPromptsChanged);
        ArgumentNullException.ThrowIfNull(setStatus);

        _catalogOptions = catalogOptions;
        _getSelectedProject = getSelectedProject;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
        _onPromptsChanged = onPromptsChanged;
        _setStatus = setStatus;
        _promptCatalog = promptCatalog ?? new AgentPromptCatalog();

        _promptList = new OptionList<PromptRow>()
            .ActivateOnClick(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        _promptList.ItemTemplate = new DataTemplate<PromptRow>(
            static (DataTemplateValue<PromptRow> value, in DataTemplateContext _) => BuildPromptRowVisual(value.GetValue()),
            null);
        _promptList.SelectionChanged((_, e) => OnAgentPromptSelectionChanged(e.OldIndex, e.NewIndex));
        _promptList.AddCommand(new Command
        {
            Id = "CodeAlta.Prompts.Delete",
            LabelMarkup = "Delete Prompt",
            DescriptionMarkup = "Delete the selected non-built-in prompt.",
            Gesture = new KeyGesture(TerminalKey.Delete),
            CanExecute = _ => !IsSelectedReadOnly() && _selectedRow is not null,
            Execute = _ => DeleteSelectedPrompt(),
        });

        _systemPromptList = new OptionList<SystemPromptRow>()
            .ActivateOnClick(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        _systemPromptList.ItemTemplate = new DataTemplate<SystemPromptRow>(
            static (DataTemplateValue<SystemPromptRow> value, in DataTemplateContext _) => BuildSystemPromptRowVisual(value.GetValue()),
            null);
        _systemPromptList.SelectionChanged((_, e) => OnSystemPromptSelectionChanged(e.OldIndex, e.NewIndex));
        _systemPromptList.AddCommand(new Command
        {
            Id = "CodeAlta.SystemPrompts.Delete",
            LabelMarkup = "Delete System Prompt",
            DescriptionMarkup = "Delete the selected non-built-in system prompt.",
            Gesture = new KeyGesture(TerminalKey.Delete),
            CanExecute = _ => !IsSelectedReadOnly() && _selectedSystemRow is not null,
            Execute = _ => DeleteSelectedPrompt(),
        });

        _nameBox = new TextBox()
            .Placeholder("Display name")
            .HorizontalAlignment(Align.Stretch)
            .IsEnabled(() => _activeTab == PromptResourceTab.AgentPrompt && !IsSelectedReadOnly());
        _descriptionBox = new TextBox()
            .Placeholder("Optional description")
            .HorizontalAlignment(Align.Stretch)
            .IsEnabled(() => _activeTab == PromptResourceTab.AgentPrompt && !IsSelectedReadOnly());
        _systemBox = new TextBox()
            .Placeholder(AgentPromptCatalog.DefaultPromptName)
            .HorizontalAlignment(Align.Stretch)
            .IsEnabled(() => _activeTab == PromptResourceTab.AgentPrompt && !IsSelectedReadOnly());
        _nameBox.TextDocument.Changed += OnEditorChanged;
        _descriptionBox.TextDocument.Changed += OnEditorChanged;
        _systemBox.TextDocument.Changed += OnEditorChanged;

        _bodyEditor = CreateMarkdownEditor(string.Empty)
            .IsEnabled(() => !IsSelectedReadOnly());
        SetBodyText(string.Empty);
    }

    public void Show()
    {
        if (_dialog is not null)
        {
            _dialog.App?.Focus(_promptList);
            return;
        }

        _dialog = BuildDialog();
        ResponsiveDialogSize.Apply(_dialog, _getBounds(), minWidth: 108, minHeight: 26, widthFactor: 0.88, heightFactor: 0.78);
        _dialog.Show();
        ReloadPrompts(selectPath: null);
        _dialog.App?.Focus(_promptList);
    }

    private Dialog BuildDialog()
    {
        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(RequestClose);

        var newGlobalButton = new Button("New global")
            .Tone(ControlTone.Primary)
            .Click(() => ShowNewPromptDialog(PromptStorageScope.Global));
        var newProjectButton = new Button("New project")
            .Tone(ControlTone.Primary)
            .IsEnabled(() => _getSelectedProject() is not null)
            .Click(() => ShowNewPromptDialog(PromptStorageScope.Project));
        var saveButton = new Button($"{TerminalIcons.MdContentSaveCheckOutline} Save")
            .Tone(ControlTone.Success)
            .IsEnabled(CanSaveSelectedPrompt)
            .Click(SaveSelectedPrompt);
        var deleteButton = new Button($"{TerminalIcons.MdTrashCanOutline} Delete")
            .Tone(ControlTone.Error)
            .IsEnabled(() => !IsSelectedReadOnly() && GetSelectedPath() is not null)
            .Click(DeleteSelectedPrompt);
        var refreshButton = new Button("Refresh")
            .Click(() => ReloadActivePrompts(GetSelectedPath()));

        var toolbar = new HStack(newGlobalButton, newProjectButton, saveButton, deleteButton, refreshButton)
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var intro = new Markup("[dim]Agent prompts are Markdown files under built-in content, ~/.alta/prompts/agents, or project .alta/prompts/agents. Later sources override earlier ids; built-ins are read-only. Use the System Prompt tab to edit only global/project system prompt overrides.[/]")
        {
            Wrap = true,
        };

        var leftPane = new VStack(
            new TextBlock("Agent Prompts"),
            new Border(new ScrollViewer(_promptList, focusable: false).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            Spacing = 0,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var systemLeftPane = new VStack(
            new TextBlock("System Prompts"),
            new Border(new ScrollViewer(_systemPromptList, focusable: false).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            Spacing = 0,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        form.Cell(new TextBlock("Name") { VerticalAlignment = Align.Center }, 0, 0);
        form.Cell(_nameBox.Stretch(), 0, 1);
        form.Cell(new TextBlock("Description") { VerticalAlignment = Align.Center }, 1, 0);
        form.Cell(_descriptionBox.Stretch(), 1, 1);
        form.Cell(new TextBlock("System") { VerticalAlignment = Align.Center }, 2, 0);
        form.Cell(_systemBox.Stretch(), 2, 1);

        var sourceSummary = new Markup(BuildSelectionSummaryMarkup)
        {
            Wrap = true,
            HorizontalAlignment = Align.Stretch,
        };
        var status = new Markup(BuildStatusMarkup)
        {
            Wrap = true,
            HorizontalAlignment = Align.Stretch,
        };
        var editorFrame = new Border(
            new ScrollViewer(_bodyEditor.Stretch(), focusable: false)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var rightPane = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        rightPane.Cell(form, 0, 0);
        rightPane.Cell(sourceSummary, 1, 0);
        rightPane.Cell(new TextBlock("Markdown body"), 2, 0);
        rightPane.Cell(editorFrame, 3, 0);
        rightPane.Cell(status, 4, 0);

        var tabs = new TabControl(
            new TabPage("Agent Prompt", leftPane),
            new TabPage("System Prompt", systemLeftPane));
        tabs.SelectionChanged((sender, e) =>
        {
            if (sender is TabControl tabControl)
            {
                OnResourceTabSelectionChanged(tabControl, e.OldIndex, e.NewIndex);
            }
        });

        var splitter = new HSplitter(tabs, rightPane)
        {
            Ratio = 0.34,
            MinFirst = 34,
            MinSecond = 60,
        };

        var content = new DockLayout()
            .Top(new VStack(toolbar, intro)
            {
                Spacing = 1,
                HorizontalAlignment = Align.Stretch,
            })
            .Content(splitter)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .Title("Agent Prompts")
            .TopRightText(closeButton)
            .BottomLeftText(new Markup("[dim]Ctrl+S Save · Ctrl+N New global · Ctrl+P New project · Delete Remove · Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Prompts.Save",
            LabelMarkup = "Save",
            DescriptionMarkup = "Save the selected prompt.",
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.CommandBar,
            Importance = CommandImportance.Primary,
            CanExecute = _ => CanSaveSelectedPrompt(),
            Execute = _ => SaveSelectedPrompt(),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Prompts.NewGlobal",
            LabelMarkup = "New Global Prompt",
            DescriptionMarkup = "Create a user-global prompt override.",
            Gesture = new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl),
            Execute = _ => ShowNewPromptDialog(PromptStorageScope.Global),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Prompts.NewProject",
            LabelMarkup = "New Project Prompt",
            DescriptionMarkup = "Create a project-local prompt override.",
            Gesture = new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl),
            CanExecute = _ => _getSelectedProject() is not null,
            Execute = _ => ShowNewPromptDialog(PromptStorageScope.Project),
        });
        dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Prompts.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the prompt manager.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => RequestClose(),
        });
        return dialog;
    }

    private void ReloadPrompts(string? selectPath)
    {
        ReloadAgentPrompts(selectPath);
        ReloadSystemPrompts(selectPath: null);
    }

    private void ReloadActivePrompts(string? selectPath)
    {
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            ReloadSystemPrompts(selectPath);
        }
        else
        {
            ReloadAgentPrompts(selectPath);
        }
    }

    private void ReloadAgentPrompts(string? selectPath)
    {
        var prompts = _promptCatalog.ListPrompts(CreateQuery());
        _rows = prompts
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(static prompt => new PromptRow(prompt))
            .ToArray();

        _loadingSelection = true;
        try
        {
            _promptList.Items.Clear();
            foreach (var row in _rows)
            {
                _promptList.Items.Add(row);
            }

            var selectedIndex = FindPromptIndex(selectPath);
            _promptList.SelectedIndex = selectedIndex;
            LoadSelectedAgentPrompt(selectedIndex);
        }
        finally
        {
            _loadingSelection = false;
        }
    }

    private void ReloadSystemPrompts(string? selectPath)
    {
        var prompts = _promptCatalog.ListSystemPrompts(CreateQuery());
        _systemRows = prompts
            .OrderBy(static prompt => prompt.Precedence)
            .ThenBy(static prompt => prompt.PromptName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(static prompt => new SystemPromptRow(prompt))
            .ToArray();

        _loadingSelection = true;
        try
        {
            _systemPromptList.Items.Clear();
            foreach (var row in _systemRows)
            {
                _systemPromptList.Items.Add(row);
            }

            var selectedIndex = FindSystemPromptIndex(selectPath);
            _systemPromptList.SelectedIndex = selectedIndex;
            if (_activeTab == PromptResourceTab.SystemPrompt)
            {
                LoadSelectedSystemPrompt(selectedIndex);
            }
        }
        finally
        {
            _loadingSelection = false;
        }
    }

    private AgentPromptCatalogQuery CreateQuery()
    {
        var project = _getSelectedProject();
        return new AgentPromptCatalogQuery
        {
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserCodeAltaRoot = _catalogOptions.GlobalRoot,
            ProjectRoot = project?.ProjectPath,
            ProjectPromptResourcesTrusted = project is not null,
        };
    }

    private int FindPromptIndex(string? sourcePath)
    {
        if (_rows.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var index = _rows.ToList().FindIndex(row => string.Equals(row.Descriptor.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return index;
            }
        }

        var defaultIndex = _rows.ToList().FindIndex(static row =>
            !row.Descriptor.IsShadowed &&
            string.Equals(row.Descriptor.PromptName, AgentPromptCatalog.DefaultPromptName, StringComparison.OrdinalIgnoreCase));
        return defaultIndex >= 0 ? defaultIndex : 0;
    }

    private int FindSystemPromptIndex(string? sourcePath)
    {
        if (_systemRows.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var index = _systemRows.ToList().FindIndex(row => string.Equals(row.Descriptor.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return index;
            }
        }

        var defaultIndex = _systemRows.ToList().FindIndex(static row =>
            !row.Descriptor.IsShadowed &&
            string.Equals(row.Descriptor.PromptName, AgentPromptCatalog.DefaultPromptName, StringComparison.OrdinalIgnoreCase));
        return defaultIndex >= 0 ? defaultIndex : 0;
    }

    private void OnAgentPromptSelectionChanged(int oldIndex, int newIndex)
    {
        if (_loadingSelection)
        {
            return;
        }

        if (HasUnsavedChanges())
        {
            _loadingSelection = true;
            try
            {
                _promptList.SelectedIndex = oldIndex;
            }
            finally
            {
                _loadingSelection = false;
            }

            new ConfirmationDialog(
                "Discard Prompt Changes?",
                ["The selected prompt has unsaved changes.", "Discard those changes and switch to the other prompt?"],
                "Discard",
                ControlTone.Error,
                () =>
                {
                    _loadingSelection = true;
                    try
                    {
                        _promptList.SelectedIndex = newIndex;
                        LoadSelectedAgentPrompt(newIndex);
                    }
                    finally
                    {
                        _loadingSelection = false;
                    }

                    return Task.CompletedTask;
                },
                _getBounds,
                () => _bodyEditor)
                .Show();
            return;
        }

        LoadSelectedAgentPrompt(newIndex);
    }

    private void OnSystemPromptSelectionChanged(int oldIndex, int newIndex)
    {
        if (_loadingSelection || _activeTab != PromptResourceTab.SystemPrompt)
        {
            return;
        }

        if (HasUnsavedChanges())
        {
            _loadingSelection = true;
            try
            {
                _systemPromptList.SelectedIndex = oldIndex;
            }
            finally
            {
                _loadingSelection = false;
            }

            new ConfirmationDialog(
                "Discard Prompt Changes?",
                ["The selected system prompt has unsaved changes.", "Discard those changes and switch to the other system prompt?"],
                "Discard",
                ControlTone.Error,
                () =>
                {
                    _loadingSelection = true;
                    try
                    {
                        _systemPromptList.SelectedIndex = newIndex;
                        LoadSelectedSystemPrompt(newIndex);
                    }
                    finally
                    {
                        _loadingSelection = false;
                    }

                    return Task.CompletedTask;
                },
                _getBounds,
                () => _bodyEditor)
                .Show();
            return;
        }

        LoadSelectedSystemPrompt(newIndex);
    }

    private void OnResourceTabSelectionChanged(TabControl tabControl, int oldIndex, int newIndex)
    {
        if (_suppressTabSelectionChanged)
        {
            return;
        }

        var nextTab = newIndex == (int)PromptResourceTab.SystemPrompt
            ? PromptResourceTab.SystemPrompt
            : PromptResourceTab.AgentPrompt;
        if (_activeTab == nextTab)
        {
            return;
        }

        if (HasUnsavedChanges())
        {
            _suppressTabSelectionChanged = true;
            try
            {
                tabControl.SelectedIndex = oldIndex;
            }
            finally
            {
                _suppressTabSelectionChanged = false;
            }

            _dialog?.App?.Focus(_bodyEditor);
            new ConfirmationDialog(
                "Discard Prompt Changes?",
                ["The selected prompt has unsaved changes.", "Discard those changes and switch tabs?"],
                "Discard",
                ControlTone.Error,
                () =>
                {
                    _suppressTabSelectionChanged = true;
                    try
                    {
                        tabControl.SelectedIndex = newIndex;
                        _activeTab = nextTab;
                        LoadSelectedAgentPromptForActiveTab();
                    }
                    finally
                    {
                        _suppressTabSelectionChanged = false;
                    }

                    return Task.CompletedTask;
                },
                _getBounds,
                () => _bodyEditor)
                .Show();
            return;
        }

        _activeTab = nextTab;
        LoadSelectedAgentPromptForActiveTab();
    }

    private void LoadSelectedAgentPromptForActiveTab()
    {
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            LoadSelectedSystemPrompt(_systemPromptList.SelectedIndex);
            _dialog?.App?.Focus(_systemPromptList);
        }
        else
        {
            LoadSelectedAgentPrompt(_promptList.SelectedIndex);
            _dialog?.App?.Focus(_promptList);
        }
    }

    private void LoadSelectedAgentPrompt(int index)
    {
        _selectedRow = (uint)index < (uint)_rows.Count ? _rows[index] : null;
        _suppressEditorChanged = true;
        try
        {
            if (_selectedRow is null)
            {
                _loadedName = string.Empty;
                _loadedDescription = null;
                _loadedSystem = AgentPromptCatalog.DefaultPromptName;
                _loadedBody = string.Empty;
                _nameBox.Text = string.Empty;
                _descriptionBox.Text = string.Empty;
                _systemBox.Text = AgentPromptCatalog.DefaultPromptName;
                SetBodyText(string.Empty);
                _statusMessage.Value = "No prompts were discovered.";
                return;
            }

            var descriptor = _selectedRow.Descriptor;
            _loadedName = descriptor.DisplayName;
            _loadedDescription = descriptor.Description;
            _loadedSystem = descriptor.SystemPromptName;
            _loadedBody = descriptor.Body;
            _nameBox.Text = descriptor.DisplayName;
            _descriptionBox.Text = descriptor.Description ?? string.Empty;
            _systemBox.Text = descriptor.SystemPromptName;
            SetBodyText(descriptor.Body);
            _statusMessage.Value = descriptor.IsBuiltIn
                ? "Built-in prompts are read-only. Create a global or project prompt with the same file id to override one."
                : null;
        }
        finally
        {
            _suppressEditorChanged = false;
            _selectionVersion.Value++;
            _editVersion.Value++;
        }
    }

    private void LoadSelectedSystemPrompt(int index)
    {
        _selectedSystemRow = (uint)index < (uint)_systemRows.Count ? _systemRows[index] : null;
        _suppressEditorChanged = true;
        try
        {
            _loadedDescription = null;
            _loadedSystem = AgentPromptCatalog.DefaultPromptName;
            if (_selectedSystemRow is null)
            {
                _loadedName = string.Empty;
                _loadedBody = string.Empty;
                _nameBox.Text = string.Empty;
                _descriptionBox.Text = string.Empty;
                _systemBox.Text = string.Empty;
                SetBodyText(string.Empty);
                _statusMessage.Value = "No system prompts were discovered.";
                return;
            }

            var descriptor = _selectedSystemRow.Descriptor;
            _loadedName = descriptor.PromptName;
            _loadedBody = descriptor.Body;
            _nameBox.Text = descriptor.PromptName;
            _descriptionBox.Text = string.Empty;
            _systemBox.Text = string.Empty;
            SetBodyText(descriptor.Body);
            _statusMessage.Value = descriptor.IsBuiltIn
                ? "Built-in system prompts are read-only. Create a global or project system prompt with the same file id to override one."
                : null;
        }
        finally
        {
            _suppressEditorChanged = false;
            _selectionVersion.Value++;
            _editVersion.Value++;
        }
    }

    private bool IsSelectedReadOnly()
    {
        _ = _selectionVersion.Value;
        return _activeTab == PromptResourceTab.SystemPrompt
            ? _selectedSystemRow?.Descriptor.IsBuiltIn != false
            : _selectedRow?.Descriptor.IsBuiltIn != false;
    }

    private bool CanSaveSelectedPrompt()
        => !IsSelectedReadOnly() && HasUnsavedChanges() && ValidateEditor(out _, out _);

    private bool HasUnsavedChanges()
    {
        _ = _editVersion.Value;
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            if (_selectedSystemRow is null || _selectedSystemRow.Descriptor.IsBuiltIn)
            {
                return false;
            }

            return !string.Equals(GetEditorText(_bodyEditor).Trim(), _loadedBody, StringComparison.Ordinal);
        }

        if (_selectedRow is null || _selectedRow.Descriptor.IsBuiltIn)
        {
            return false;
        }

        return !string.Equals(NormalizeRequiredText(_nameBox.Text), _loadedName, StringComparison.Ordinal) ||
               !string.Equals(NormalizeOptionalText(_descriptionBox.Text), NormalizeOptionalText(_loadedDescription), StringComparison.Ordinal) ||
               !string.Equals(NormalizeSystemName(_systemBox.Text), _loadedSystem, StringComparison.Ordinal) ||
               !string.Equals(GetEditorText(_bodyEditor).Trim(), _loadedBody, StringComparison.Ordinal);
    }

    private void SaveSelectedPrompt()
    {
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            SaveSelectedSystemPrompt();
            return;
        }

        if (_selectedRow is not { } row || row.Descriptor.IsBuiltIn)
        {
            SetDialogStatus("Built-in prompts are read-only.", StatusTone.Warning);
            return;
        }

        if (!ValidateEditor(out var validationMessage, out var values))
        {
            SetDialogStatus(validationMessage, StatusTone.Error);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(row.Descriptor.SourcePath)!);
            File.WriteAllText(row.Descriptor.SourcePath, BuildPromptFile(values));
            SetDialogStatus($"Saved prompt '{values.Name}'.", StatusTone.Ready);
            _setStatus($"Saved prompt '{values.Name}'.", StatusTone.Ready);
            _onPromptsChanged();
            ReloadAgentPrompts(row.Descriptor.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetDialogStatus($"Failed to save prompt: {ex.Message}", StatusTone.Error);
        }
    }

    private void SaveSelectedSystemPrompt()
    {
        if (_selectedSystemRow is not { } row || row.Descriptor.IsBuiltIn)
        {
            SetDialogStatus("Built-in system prompts are read-only.", StatusTone.Warning);
            return;
        }

        var body = GetEditorText(_bodyEditor).Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            SetDialogStatus("System prompt body is required.", StatusTone.Error);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(row.Descriptor.SourcePath)!);
            File.WriteAllText(row.Descriptor.SourcePath, BuildSystemPromptFile(body));
            SetDialogStatus($"Saved system prompt '{row.Descriptor.PromptName}'.", StatusTone.Ready);
            _setStatus($"Saved system prompt '{row.Descriptor.PromptName}'.", StatusTone.Ready);
            _onPromptsChanged();
            ReloadSystemPrompts(row.Descriptor.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetDialogStatus($"Failed to save system prompt: {ex.Message}", StatusTone.Error);
        }
    }

    private void DeleteSelectedPrompt()
    {
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            DeleteSelectedSystemPrompt();
            return;
        }

        if (_selectedRow is not { } row)
        {
            return;
        }

        if (row.Descriptor.IsBuiltIn)
        {
            SetDialogStatus("Built-in prompts are read-only and cannot be deleted.", StatusTone.Warning);
            return;
        }

        new ConfirmationDialog(
            "Delete Prompt?",
            [$"Delete '{row.Descriptor.DisplayName}' from {row.Descriptor.SourcePath}?", "This removes only the selected global/project prompt file."],
            "Delete",
            ControlTone.Error,
            () =>
            {
                try
                {
                    File.Delete(row.Descriptor.SourcePath);
                    SetDialogStatus($"Deleted prompt '{row.Descriptor.DisplayName}'.", StatusTone.Ready);
                    _setStatus($"Deleted prompt '{row.Descriptor.DisplayName}'.", StatusTone.Ready);
                    _onPromptsChanged();
                    ReloadAgentPrompts(selectPath: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SetDialogStatus($"Failed to delete prompt: {ex.Message}", StatusTone.Error);
                }

                return Task.CompletedTask;
            },
            _getBounds,
            () => _promptList)
            .Show();
    }

    private void DeleteSelectedSystemPrompt()
    {
        if (_selectedSystemRow is not { } row)
        {
            return;
        }

        if (row.Descriptor.IsBuiltIn)
        {
            SetDialogStatus("Built-in system prompts are read-only and cannot be deleted.", StatusTone.Warning);
            return;
        }

        new ConfirmationDialog(
            "Delete System Prompt?",
            [$"Delete system prompt '{row.Descriptor.PromptName}' from {row.Descriptor.SourcePath}?", "This removes only the selected global/project system prompt file."],
            "Delete",
            ControlTone.Error,
            () =>
            {
                try
                {
                    File.Delete(row.Descriptor.SourcePath);
                    SetDialogStatus($"Deleted system prompt '{row.Descriptor.PromptName}'.", StatusTone.Ready);
                    _setStatus($"Deleted system prompt '{row.Descriptor.PromptName}'.", StatusTone.Ready);
                    _onPromptsChanged();
                    ReloadSystemPrompts(selectPath: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SetDialogStatus($"Failed to delete system prompt: {ex.Message}", StatusTone.Error);
                }

                return Task.CompletedTask;
            },
            _getBounds,
            () => _systemPromptList)
            .Show();
    }

    private void ShowNewPromptDialog(PromptStorageScope scope)
    {
        var resourceTab = _activeTab;
        if (scope == PromptStorageScope.Project && _getSelectedProject() is null)
        {
            SetDialogStatus($"Select a project before creating a project {ResourceLabel(resourceTab)}.", StatusTone.Warning);
            return;
        }

        var idBox = new TextBox().Placeholder(resourceTab == PromptResourceTab.SystemPrompt ? "system-prompt-id" : "prompt-id").HorizontalAlignment(Align.Stretch);
        var nameBox = new TextBox().Placeholder("Display name").HorizontalAlignment(Align.Stretch);
        var descriptionBox = new TextBox().Placeholder("Optional description").HorizontalAlignment(Align.Stretch);
        var systemBox = new TextBox(AgentPromptCatalog.DefaultPromptName).Placeholder(AgentPromptCatalog.DefaultPromptName).HorizontalAlignment(Align.Stretch);
        TextBlock? validationText = null;
        validationText = new TextBlock(string.Empty)
        {
            Wrap = true,
        }.Style(() => TextBlockStyle.Default with { Foreground = validationText!.GetTheme().Error ?? validationText!.GetTheme().Foreground ?? Color.Default });

        var targetDirectory = ResolveResourceDirectory(scope, resourceTab);
        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        form.Cell(new TextBlock("File id"), 0, 0);
        form.Cell(idBox, 0, 1);
        if (resourceTab == PromptResourceTab.AgentPrompt)
        {
            form.Cell(new TextBlock("Name"), 1, 0);
            form.Cell(nameBox, 1, 1);
            form.Cell(new TextBlock("Description"), 2, 0);
            form.Cell(descriptionBox, 2, 1);
            form.Cell(new TextBlock("System"), 3, 0);
            form.Cell(systemBox, 3, 1);
        }

        form.Cell(validationText, 4, 0, columnSpan: 2);

        Dialog? createDialog = null;
        var createButton = new Button("Create")
            .Tone(ControlTone.Success)
            .Click(() => CreatePromptFromDialog(createDialog, scope, resourceTab, idBox, nameBox, descriptionBox, systemBox, validationText));
        var cancelButton = new Button("Cancel").Click(() => createDialog?.Close());
        var content = new VStack(
            new Markup($"[dim]Create a {ScopeLabel(scope)} {ResourceLabel(resourceTab)} in {AnsiMarkup.Escape(targetDirectory)}. The file id becomes <id>{ResourceSuffix(resourceTab)}; use the same id as a built-in/global prompt to override it.[/]") { Wrap = true },
            form,
            new HStack(cancelButton, createButton)
            {
                HorizontalAlignment = Align.End,
                Spacing = 2,
            })
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        createDialog = new Dialog()
            .Title($"New {ScopeTitle(scope)} {ResourceTitle(resourceTab)}")
            .BottomRightText(new Markup("[dim]Esc Cancel[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(createDialog, _getBounds(), minWidth: 74, minHeight: 15, widthFactor: 0.50, heightFactor: 0.36);
        createDialog.AddCommand(new Command
        {
            Id = "CodeAlta.Prompts.New.Close",
            LabelMarkup = "Cancel",
            DescriptionMarkup = "Close the new prompt dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => createDialog.Close(),
        });
        createDialog.Show();
        createDialog.App?.Focus(idBox);
    }

    private void CreatePromptFromDialog(
        Dialog? createDialog,
        PromptStorageScope scope,
        PromptResourceTab resourceTab,
        TextBox idBox,
        TextBox nameBox,
        TextBox descriptionBox,
        TextBox systemBox,
        TextBlock validationText)
    {
        if (createDialog is null)
        {
            return;
        }

        var promptId = NormalizePromptId(idBox.Text, resourceTab);
        if (promptId is null)
        {
            validationText.Text = "Enter a file id containing only letters, digits, '.', '_', or '-'.";
            return;
        }

        var name = resourceTab == PromptResourceTab.AgentPrompt ? NormalizeRequiredText(nameBox.Text) : promptId;
        if (resourceTab == PromptResourceTab.AgentPrompt && name is null)
        {
            validationText.Text = "Enter the required prompt display name.";
            return;
        }

        var targetDirectory = ResolveResourceDirectory(scope, resourceTab);
        var path = Path.Combine(targetDirectory, promptId + ResourceSuffix(resourceTab));
        if (File.Exists(path))
        {
            validationText.Text = $"A prompt file already exists at {path}.";
            return;
        }

        var body = resourceTab == PromptResourceTab.SystemPrompt
            ? "Describe the base system instructions CodeAlta should use for sessions that select this system prompt."
            : "Describe how CodeAlta should handle sessions that use this prompt.";
        var values = new PromptEditorValues(
            name!,
            NormalizeOptionalText(descriptionBox.Text),
            NormalizeSystemName(systemBox.Text),
            body);
        try
        {
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(path, resourceTab == PromptResourceTab.SystemPrompt ? BuildSystemPromptFile(values.Body) : BuildPromptFile(values));
            createDialog.Close();
            SetDialogStatus($"Created {ResourceLabel(resourceTab)} '{name}'.", StatusTone.Ready);
            _setStatus($"Created {ResourceLabel(resourceTab)} '{name}'.", StatusTone.Ready);
            _onPromptsChanged();
            ReloadActivePrompts(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            validationText.Text = $"Failed to create prompt: {ex.Message}";
        }
    }

    private string ResolvePromptDirectory(PromptStorageScope scope)
    {
        var query = CreateQuery();
        var promptsRoot = scope == PromptStorageScope.Project
            ? _promptCatalog.ResolveProjectPromptDirectory(query) ?? _promptCatalog.ResolveGlobalPromptDirectory(query)
            : _promptCatalog.ResolveGlobalPromptDirectory(query);
        return Path.Combine(promptsRoot, "agents");
    }

    private string ResolveResourceDirectory(PromptStorageScope scope, PromptResourceTab resourceTab)
    {
        if (resourceTab == PromptResourceTab.AgentPrompt)
        {
            return ResolvePromptDirectory(scope);
        }

        var query = CreateQuery();
        return scope == PromptStorageScope.Project
            ? _promptCatalog.ResolveProjectSystemPromptDirectory(query) ?? _promptCatalog.ResolveGlobalSystemPromptDirectory(query)
            : _promptCatalog.ResolveGlobalSystemPromptDirectory(query);
    }

    private bool ValidateEditor(out string validationMessage, out PromptEditorValues values)
    {
        values = default;
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            var systemBody = GetEditorText(_bodyEditor).Trim();
            if (string.IsNullOrWhiteSpace(systemBody))
            {
                validationMessage = "System prompt body is required.";
                return false;
            }

            values = new PromptEditorValues(_loadedName, null, AgentPromptCatalog.DefaultPromptName, systemBody);
            validationMessage = string.Empty;
            return true;
        }

        var name = NormalizeRequiredText(_nameBox.Text);
        if (name is null)
        {
            validationMessage = "Prompt name is required.";
            return false;
        }

        var body = GetEditorText(_bodyEditor).Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            validationMessage = "Prompt body is required.";
            return false;
        }

        values = new PromptEditorValues(
            name,
            NormalizeOptionalText(_descriptionBox.Text),
            NormalizeSystemName(_systemBox.Text),
            body);
        validationMessage = string.Empty;
        return true;
    }

    private string BuildSelectionSummaryMarkup()
    {
        _ = _selectionVersion.Value;
        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            if (_selectedSystemRow is not { } systemRow)
            {
                return "[dim]No system prompt selected.[/]";
            }

            var systemDescriptor = systemRow.Descriptor;
            var systemReadOnly = systemDescriptor.IsBuiltIn ? " [warning]read-only[/]" : string.Empty;
            var systemShadowed = systemDescriptor.IsShadowed && systemDescriptor.ShadowedByPath is not null
                ? $" [warning]shadowed by {AnsiMarkup.Escape(systemDescriptor.ShadowedByPath)}[/]"
                : string.Empty;
            return $"[dim]id:[/] {AnsiMarkup.Escape(systemDescriptor.PromptName)}  [dim]source:[/] {AnsiMarkup.Escape(AgentPromptPresentation.ToSourceLabel(systemDescriptor.SourceKind))}{systemReadOnly}{systemShadowed}\n[dim]path:[/] {AnsiMarkup.Escape(systemDescriptor.SourcePath)}";
        }

        if (_selectedRow is not { } row)
        {
            return "[dim]No prompt selected.[/]";
        }

        var descriptor = row.Descriptor;
        var readOnly = descriptor.IsBuiltIn ? " [warning]read-only[/]" : string.Empty;
        var shadowed = descriptor.IsShadowed && descriptor.ShadowedByPath is not null
            ? $" [warning]shadowed by {AnsiMarkup.Escape(descriptor.ShadowedByPath)}[/]"
            : string.Empty;
        return $"[dim]id:[/] {AnsiMarkup.Escape(descriptor.PromptName)}  [dim]source:[/] {AnsiMarkup.Escape(AgentPromptPresentation.ToSourceLabel(descriptor.SourceKind))}{readOnly}{shadowed}\n[dim]path:[/] {AnsiMarkup.Escape(descriptor.SourcePath)}";
    }

    private string BuildStatusMarkup()
    {
        _ = _editVersion.Value;
        if (_statusMessage.Value is { } statusMessage)
        {
            return AnsiMarkup.Escape(statusMessage);
        }

        if (_activeTab == PromptResourceTab.SystemPrompt)
        {
            if (_selectedSystemRow is null)
            {
                return "[dim]Create a global or project system prompt override to get started.[/]";
            }

            if (_selectedSystemRow.Descriptor.IsBuiltIn)
            {
                return "[dim]Built-in system prompt content is read-only.[/]";
            }

            if (!ValidateEditor(out var systemValidationMessage, out _))
            {
                return $"[error]{AnsiMarkup.Escape(systemValidationMessage)}[/]";
            }

            return HasUnsavedChanges()
                ? "[warning]Unsaved system prompt changes.[/]"
                : "[success]System prompt matches the loaded file.[/]";
        }

        if (_selectedRow is null)
        {
            return "[dim]Create a global or project prompt to get started.[/]";
        }

        if (_selectedRow.Descriptor.IsBuiltIn)
        {
            return "[dim]Built-in prompt content is read-only.[/]";
        }

        if (!ValidateEditor(out var validationMessage, out _))
        {
            return $"[error]{AnsiMarkup.Escape(validationMessage)}[/]";
        }

        return HasUnsavedChanges()
            ? "[warning]Unsaved prompt changes.[/]"
            : "[success]Prompt matches the loaded file.[/]";
    }

    private void SetDialogStatus(string message, StatusTone tone)
    {
        _ = tone;
        _statusMessage.Value = message;
        _editVersion.Value++;
    }

    private void OnEditorChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_suppressEditorChanged)
        {
            return;
        }

        _statusMessage.Value = null;
        _editVersion.Value++;
    }

    private void SetBodyText(string text)
    {
        _bodyEditor.TextDocument.Changed -= OnEditorChanged;
        _bodyEditor.TextDocument = new TextDocument(text ?? string.Empty);
        _bodyEditor.SyntaxHighlighter = CodeEditorFactory.CreateSyntaxHighlighter(PromptEditorFileName, PromptEditorLanguageId);
        _bodyEditor.TextDocument.Changed += OnEditorChanged;
    }

    private void RequestClose()
    {
        if (!HasUnsavedChanges())
        {
            Close();
            return;
        }

        new ConfirmationDialog(
            "Discard Prompt Changes?",
            ["The selected prompt has unsaved changes.", "Close the prompt manager without saving them?"],
            "Discard",
            ControlTone.Error,
            () =>
            {
                Close();
                return Task.CompletedTask;
            },
            _getBounds,
            () => _bodyEditor)
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

    private static Visual BuildPromptRowVisual(PromptRow row)
    {
        var descriptor = row.Descriptor;
        var source = AgentPromptPresentation.ToSourceLabel(descriptor.SourceKind);
        var title = descriptor.IsShadowed
            ? $"[dim]{AnsiMarkup.Escape(descriptor.DisplayName)}[/]"
            : AnsiMarkup.Escape(descriptor.DisplayName);

        var status = descriptor.IsShadowed ? " shadowed" : descriptor.IsBuiltIn ? " read-only" : string.Empty;
        return new OptionListItem(
            new Markup(title) { Wrap = false },
            new Markup($"[dim]{AnsiMarkup.Escape(source)}{AnsiMarkup.Escape(status)}[/]") { Wrap = false },
            new Markup($"[dim]{AnsiMarkup.Escape(descriptor.PromptName)} · system:{AnsiMarkup.Escape(descriptor.SystemPromptName)}[/]") { Wrap = false })
        {
            SearchText = $"{descriptor.DisplayName} {descriptor.PromptName} {descriptor.Description} {source}",
        };
    }

    private static Visual BuildSystemPromptRowVisual(SystemPromptRow row)
    {
        var descriptor = row.Descriptor;
        var source = AgentPromptPresentation.ToSourceLabel(descriptor.SourceKind);
        var title = descriptor.IsShadowed
            ? $"[dim]{AnsiMarkup.Escape(descriptor.PromptName)}[/]"
            : AnsiMarkup.Escape(descriptor.PromptName);

        var status = descriptor.IsShadowed ? " shadowed" : descriptor.IsBuiltIn ? " read-only" : string.Empty;
        return new OptionListItem(
            new Markup(title) { Wrap = false },
            new Markup($"[dim]{AnsiMarkup.Escape(source)}{AnsiMarkup.Escape(status)}[/]") { Wrap = false },
            new Markup($"[dim]{AnsiMarkup.Escape(descriptor.PromptName)}[/]") { Wrap = false })
        {
            SearchText = $"{descriptor.PromptName} {source}",
        };
    }

    private static CodeEditor CreateMarkdownEditor(string text)
        => CodeEditorFactory.Create(
            text,
            new CodeEditorFactoryOptions
            {
                FileName = PromptEditorFileName,
                LanguageId = PromptEditorLanguageId,
                MinHeight = 10,
            });

    private static string GetEditorText(CodeEditor editor)
    {
        return CodeEditorFactory.GetText(editor);
    }

    private static string BuildPromptFile(PromptEditorValues values)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.Append("name: ").AppendLine(ToYamlScalar(values.Name));
        if (!string.IsNullOrWhiteSpace(values.Description))
        {
            builder.Append("description: ").AppendLine(ToYamlScalar(values.Description!));
        }

        if (!string.Equals(values.SystemPromptName, AgentPromptCatalog.DefaultPromptName, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("system: ").AppendLine(ToYamlScalar(values.SystemPromptName));
        }

        builder.AppendLine("---");
        builder.AppendLine(values.Body.Trim());
        return builder.ToString();
    }

    private static string BuildSystemPromptFile(string body)
        => body.Trim() + Environment.NewLine;

    private static string ToYamlScalar(string value)
    {
        var mustQuote = value.Length == 0 ||
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[^1]) ||
            value.Any(static ch => ch is ':' or '#' or '\'' or '"' or '[' or ']' or '{' or '}' or ',');
        if (mustQuote)
        {
            return '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
        }

        return value;
    }

    private string? GetSelectedPath()
        => _activeTab == PromptResourceTab.SystemPrompt
            ? _selectedSystemRow?.Descriptor.SourcePath
            : _selectedRow?.Descriptor.SourcePath;

    private static string? NormalizePromptId(string? value, PromptResourceTab resourceTab = PromptResourceTab.AgentPrompt)
    {
        var id = NormalizeRequiredText(value);
        if (id is null)
        {
            return null;
        }

        if (id.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^".prompt.md".Length];
        }
        else if (resourceTab == PromptResourceTab.SystemPrompt && id.EndsWith(".system-prompt.md", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^".system-prompt.md".Length];
        }

        if (id is "." or ".." || id.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        foreach (var ch in id)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.')
            {
                return null;
            }
        }

        return id;
    }

    private static string? NormalizeRequiredText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeSystemName(string? value)
        => NormalizeRequiredText(value) ?? AgentPromptCatalog.DefaultPromptName;

    private static string ScopeLabel(PromptStorageScope scope)
        => scope == PromptStorageScope.Project ? "project" : "global";

    private static string ScopeTitle(PromptStorageScope scope)
        => scope == PromptStorageScope.Project ? "Project" : "Global";

    private static string ResourceLabel(PromptResourceTab resourceTab)
        => resourceTab == PromptResourceTab.SystemPrompt ? "system prompt" : "prompt";

    private static string ResourceTitle(PromptResourceTab resourceTab)
        => resourceTab == PromptResourceTab.SystemPrompt ? "System Prompt" : "Prompt";

    private static string ResourceSuffix(PromptResourceTab resourceTab)
        => resourceTab == PromptResourceTab.SystemPrompt ? ".system-prompt.md" : ".prompt.md";

    private sealed record PromptRow(AgentPromptDescriptor Descriptor);

    private sealed record SystemPromptRow(SystemPromptDescriptor Descriptor);

    private readonly record struct PromptEditorValues(string Name, string? Description, string SystemPromptName, string Body);

    private enum PromptStorageScope
    {
        Global,
        Project,
    }

    private enum PromptResourceTab
    {
        AgentPrompt = 0,
        SystemPrompt = 1,
    }
}
