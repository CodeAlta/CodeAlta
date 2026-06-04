using System.Globalization;
using System.Text;
using CodeAlta.App;
using CodeAlta.Catalog.Skills;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Collections;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class SkillsManagementDialog
{
    private static readonly ScopeOption[] ScopeOptions =
    [
        new(SkillsManagementScope.Combined, "Combined"),
        new(SkillsManagementScope.CurrentProject, "Current Project"),
        new(SkillsManagementScope.User, "User"),
    ];

    private static readonly BulkScopeOption[] BulkScopeOptions =
    [
        new(SkillEnablementScope.Global, "Global"),
        new(SkillEnablementScope.Project, "Project"),
        new(SkillEnablementScope.Both, "Both"),
    ];

    private const int SkillGridColumnCount = 5;

    private readonly SkillsManagementService _service;
    private readonly Func<string, CancellationToken, Task> _openFileAsync;
    private readonly Func<string, CancellationToken, Task> _activateSkillAsync;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly Select<ScopeOption> _scopeSelect;
    private readonly Select<BulkScopeOption> _bulkScopeSelect;
    private readonly TextBox _filterBox;
    private readonly DataGridListDocument<SkillManagementRowViewModel> _skillDocument;
    private readonly DataGridControl _skillGrid;
    private readonly ListBox<SkillRelatedFileRow> _relatedFileList;
    private readonly BindableList<SkillRelatedFileRow> _relatedFileItems;
    private readonly Markup _summaryMarkup;
    private readonly MarkdownControl _detailMarkdown;
    private IReadOnlyList<SkillManagementRowViewModel> _allRows = [];
    private IReadOnlyList<SkillManagementRowViewModel> _rows = [];
    private int _skillDocumentRowCount;
    private string _summaryText = "[dim]Open, activate, and author filesystem skills.[/]";
    private string _relatedFilesHeaderText = "[dim]Select a skill to inspect related files.[/]";

    public SkillsManagementDialog(
        SkillsManagementService service,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<string, CancellationToken, Task> activateSkillAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        ArgumentNullException.ThrowIfNull(activateSkillAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _service = service;
        _openFileAsync = openFileAsync;
        _activateSkillAsync = activateSkillAsync;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(Close);

        _scopeSelect = new Select<ScopeOption>()
            .MinWidth(18);
        foreach (var option in ScopeOptions)
        {
            _scopeSelect.Items.Add(option);
        }

        _scopeSelect.SelectedIndex = 0;
        _scopeSelect.SelectionChanged((_, _) => StartReload());

        _bulkScopeSelect = new Select<BulkScopeOption>()
            .MinWidth(10);
        foreach (var option in BulkScopeOptions)
        {
            _bulkScopeSelect.Items.Add(option);
        }

        _bulkScopeSelect.SelectedIndex = 0;

        _filterBox = new TextBox()
            .Placeholder("Filter by name, description, source, or path")
            .HorizontalAlignment(Align.Stretch);
        _filterBox.TextDocument.Changed += OnFilterTextChanged;

        _skillDocument = new DataGridListDocument<SkillManagementRowViewModel>();
        using (_skillDocument.BeginUpdate())
        {
            _skillDocument
                .AddColumn(new DataGridColumnInfo<bool>("global", "G", false, SkillManagementRowViewModel.Accessor.GlobalEnabled))
                .AddColumn(new DataGridColumnInfo<bool>("project", "P", !_service.HasSelectedProject, SkillManagementRowViewModel.Accessor.ProjectEnabled))
                .AddColumn(new DataGridColumnInfo<string>("builtin", "Built-in", true, SkillManagementRowViewModel.Accessor.BuiltIn))
                .AddColumn(new DataGridColumnInfo<string>("name", "Skill", true, SkillManagementRowViewModel.Accessor.Name))
                .AddColumn(new DataGridColumnInfo<string>("status", "Status", true, SkillManagementRowViewModel.Accessor.Status));
        }

        _skillGrid = new DataGridControl(_skillDocument)
            .SelectionMode(DataGridSelectionMode.Row)
            .EditMode(DataGridEditMode.OnEnter)
            .CellActivationMode(DataGridCellActivationMode.Auto)
            .ReadOnly(false)
            .FilterRowVisible(false)
            .ShowHeader(true)
            .ShowRowAnchor(false)
            .MinWidth(38)
            .Stretch();
        ConfigureSkillGridColumns(_skillGrid, _service.HasSelectedProject);
        BindingManager.Current.ValueChanged += OnBindingValueChanged;

        _relatedFileList = new ListBox<SkillRelatedFileRow>()
            .MinHeight(4)
            .Stretch();
        _relatedFileItems = _relatedFileList.Items;
        _relatedFileList.SelectedIndex = -1;

        _summaryMarkup = new Markup(() => _summaryText)
        {
            Wrap = true,
        };

        _detailMarkdown = new MarkdownControl()
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Options = MarkdownRenderOptions.Default with
            {
                CodeBlockRenderer = new TextMateMarkdownCodeBlockRenderer(),
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = 16,
            },
        };
        _detailMarkdown.Markdown = BuildDetailMarkdown(null, relatedFileCount: 0);

        var refreshButton = new Button("Refresh")
            .Tone(ControlTone.Primary)
            .Click(StartReload);
        var newSkillButton = new Button("New skill")
            .Tone(ControlTone.Primary)
            .Click(ShowNewSkillDialog);
        var activateButton = new Button("Activate")
            .Tone(ControlTone.Success)
            .Click(() => _ = ActivateSelectedSkillAsync());
        var openSkillButton = new Button("Open SKILL.md")
            .Click(() => _ = OpenSelectedSkillAsync());
        var openRelatedButton = new Button("Open related")
            .Click(() => _ = OpenSelectedRelatedFileAsync());

        var enableAllButton = new Button("Enable")
            .Tone(ControlTone.Success)
            .Click(() => _ = ApplyBulkEnablementAsync(enabled: true));
        var disableAllButton = new Button("Disable")
            .Tone(ControlTone.Warning)
            .Click(() => _ = ApplyBulkEnablementAsync(enabled: false));
        var invertButton = new Button("Invert")
            .Click(() => _ = InvertBulkEnablementAsync());

        var toolbar = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Cell(new TextBlock("Scope") { VerticalAlignment = Align.Center }, 0, 0);
        toolbar.Cell(_scopeSelect, 0, 1);
        toolbar.Cell(new TextBlock("Filter") { VerticalAlignment = Align.Center }, 0, 2);
        toolbar.Cell(_filterBox, 0, 3);
        toolbar.Cell(
            new HStack(
                Tooltip(newSkillButton, "Create a new filesystem skill template."),
                Tooltip(activateButton, "Activate the selected valid, unshadowed skill in the current session."),
                Tooltip(openSkillButton, "Open the selected skill's SKILL.md file."),
                Tooltip(openRelatedButton, "Open the selected related script, reference, or asset file."),
                Tooltip(refreshButton, "Reload discovered skills for the selected scope."))
            {
                HorizontalAlignment = Align.End,
                Spacing = 1,
            },
            0,
            4);

        var introText = new Markup("[dim]Browse skills, manage global/project enablement for the shown list, activate enabled skills, or open SKILL.md and related files. G/P are editable enablement columns; Built-in marks built-in skills.[/]")
        {
            Wrap = true,
        };

        var detailPane = new Border(_detailMarkdown.Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch);
        var relatedPane = new DockLayout(
            top: new Markup(() => _relatedFilesHeaderText) { Wrap = false },
            content: new Border(new ScrollViewer(_relatedFileList).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            bottom: null)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        var rightPane = new TabControl(
            new TabPage("Summary", detailPane),
            new TabPage("Related files", relatedPane))
        {
            AllowTabDragReorder = false,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var bulkActions = new WrapHStack(
            new TextBlock("Bulk") { VerticalAlignment = Align.Center },
            _bulkScopeSelect,
            Tooltip(enableAllButton, "Enable the currently shown skills for the selected config scope."),
            Tooltip(disableAllButton, "Disable the currently shown skills for the selected config scope."),
            Tooltip(invertButton, "Invert the currently shown skills for the selected config scope."))
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
            RunSpacing = 0,
        };
        var leftPane = new DockLayout(
            top: bulkActions,
            content: new Border(new ScrollViewer(_skillGrid).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            bottom: null)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        var splitter = new HSplitter(leftPane, rightPane)
        {
            Ratio = 0.34,
            MinFirst = 30,
            MinSecond = 54,
        };

        var contentGrid = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });

        contentGrid.Cell(toolbar, 0, 0);
        contentGrid.Cell(introText, 1, 0);
        contentGrid.Cell(_summaryMarkup, 2, 0);
        contentGrid.Cell(splitter, 3, 0);

        _dialog = new Dialog()
            .Title("Skills")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(contentGrid);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 104, minHeight: 24, widthFactor: 0.86, heightFactor: 0.76);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Skills.Manage.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the skills browser.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
    {
        _dialog.Show();
        StartReload();
        _dialog.App?.Focus(_skillGrid);
    }

    private void StartReload()
        => _ = ReloadAsync(selectFirst: true);

    private void OnBindingValueChanged(Binding binding)
    {
        if (ReferenceEquals(binding.Owner, _skillGrid) && string.Equals(binding.Accessor.Name, "CurrentCell", StringComparison.Ordinal))
        {
            RefreshSelectedSkillDetails();
        }
    }

    private async Task ReloadAsync(string? preferredSkillName = null, bool selectFirst = false)
    {
        _summaryText = "[primary]Loading skills...[/]";
        var scope = GetSelectedScope();
        preferredSkillName ??= GetSelectedDescriptor()?.Name;
        try
        {
            var descriptors = await Task.Run(() => _service.LoadAsync(scope));
            await _dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    _allRows = descriptors
                        .OrderBy(static descriptor => descriptor.Precedence)
                        .ThenBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(CreateSkillRow)
                        .ToArray();

                    ApplyFilter(selectFirst: selectFirst && string.IsNullOrWhiteSpace(preferredSkillName), preferredSkillName);
                });
        }
        catch (Exception ex)
        {
            await _dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    _allRows = [];
                    _rows = [];
                    SyncSkillItems(_rows);
                    SetSelectedSkillIndex(-1);
                    _summaryText = $"[error]Failed to load skills: {AnsiMarkup.Escape(ex.Message)}[/]";
                    RefreshSelectedSkillDetails();
                });
        }
    }

    private void OnFilterTextChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyFilter(selectFirst: true);
    }

    private void ApplyFilter(bool selectFirst, string? preferredSkillName = null)
    {
        var selectedName = preferredSkillName ?? GetSelectedDescriptor()?.Name;
        var filterText = (_filterBox.Text ?? string.Empty).Trim();
        _rows = string.IsNullOrWhiteSpace(filterText)
            ? _allRows
            : _allRows
                .Where(row => row.Matches(filterText))
                .ToArray();

        var selectedIndex = ResolveSelectedSkillIndex(selectFirst, selectedName);
        SyncSkillItems(_rows);
        SetSelectedSkillIndex(selectedIndex);

        _summaryText = BuildSummaryMarkup(_allRows.Select(static row => row.Descriptor).ToArray(), _rows.Count, filterText);
    }

    private int ResolveSelectedSkillIndex(bool selectFirst, string? selectedName)
    {
        if (_rows.Count == 0)
        {
            return -1;
        }

        if (selectFirst)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            return FindSkillIndex(selectedName);
        }

        var currentIndex = GetSelectedSkillIndex();
        return (uint)currentIndex < (uint)_rows.Count
            ? currentIndex
            : _rows.Count - 1;
    }

    private void SyncSkillItems(IReadOnlyList<SkillManagementRowViewModel> rows)
    {
        using var _ = _skillDocument.BeginUpdate();
        var commonCount = Math.Min(_skillDocumentRowCount, rows.Count);
        for (var i = 0; i < commonCount; i++)
        {
            var existing = _skillDocument.Rows[i];
            var next = rows[i];
            if (string.Equals(existing.SkillKey, next.SkillKey, StringComparison.OrdinalIgnoreCase))
            {
                existing.UpdateFromDescriptor(next.Descriptor);
            }
            else
            {
                _skillDocument.ReplaceRow(i, next);
            }
        }

        if (_skillDocumentRowCount > rows.Count)
        {
            _skillDocument.RemoveRows(rows.Count, _skillDocumentRowCount - rows.Count);
        }

        for (var i = commonCount; i < rows.Count; i++)
        {
            _skillDocument.AddRow(rows[i]);
        }

        _skillDocumentRowCount = rows.Count;
    }

    private void SetSelectedSkillIndex(int index)
    {
        var oldIndex = GetSelectedSkillIndex();
        if ((uint)index >= (uint)_rows.Count)
        {
            _skillGrid.SelectedRow = -1;
            _skillGrid.CurrentCell = DataGridCell.None;
        }
        else
        {
            _skillGrid.SelectedRow = -1;
            var currentColumn = _skillGrid.CurrentCell == DataGridCell.None
                ? 0
                : Math.Clamp(_skillGrid.CurrentCell.Column, 0, SkillGridColumnCount - 1);
            _skillGrid.CurrentCell = new DataGridCell(index, currentColumn);
        }

        if (GetSelectedSkillIndex() == oldIndex)
        {
            RefreshSelectedSkillDetails();
        }
    }

    private int GetSelectedSkillIndex()
    {
        var index = _skillGrid.CurrentCell.Row;
        return (uint)index < (uint)_rows.Count ? index : -1;
    }

    private void RefreshSelectedSkillDetails()
    {
        var descriptor = GetSelectedDescriptor();
        _relatedFileItems.Clear();
        if (descriptor is null)
        {
            _relatedFileList.SelectedIndex = -1;
            _relatedFilesHeaderText = "[dim]Select a skill to inspect related files.[/]";
            _detailMarkdown.Markdown = BuildDetailMarkdown(null, relatedFileCount: 0);
            return;
        }

        var relatedFiles = _service.ListRelatedFiles(descriptor);
        foreach (var file in relatedFiles)
        {
            _relatedFileItems.Add(new SkillRelatedFileRow(file));
        }

        _relatedFileList.SelectedIndex = _relatedFileItems.Count > 0 ? 0 : -1;
        _relatedFilesHeaderText = BuildRelatedFilesHeaderMarkup(relatedFiles.Count);
        _detailMarkdown.Markdown = BuildDetailMarkdown(descriptor, relatedFiles.Count);
    }

    private async Task OpenSelectedSkillAsync()
    {
        if (GetSelectedDescriptor() is not { } descriptor)
        {
            return;
        }

        await _openFileAsync(descriptor.SkillFilePath, CancellationToken.None);
    }

    private async Task OpenSelectedRelatedFileAsync()
    {
        var index = _relatedFileList.SelectedIndex;
        if ((uint)index >= (uint)_relatedFileItems.Count)
        {
            _summaryText = "[warning]Select a related file before opening.[/]";
            return;
        }

        await _openFileAsync(_relatedFileItems[index].File.FullPath, CancellationToken.None);
    }

    private void ShowNewSkillDialog()
    {
        var nameBox = new TextBox()
            .Placeholder("lowercase-name");
        var descriptionBox = new TextBox()
            .Placeholder("Describe when this skill should be used")
            .HorizontalAlignment(Align.Stretch);
        TextBlock? validationText = null;
        validationText = new TextBlock(string.Empty)
        {
            Wrap = true,
        }.Style(() => TextBlockStyle.Default with { Foreground = validationText!.GetTheme().Error ?? validationText!.GetTheme().Foreground ?? Color.Default });

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        form.Cell(new TextBlock("Name"), 0, 0);
        form.Cell(nameBox.Stretch(), 0, 1);
        form.Cell(new TextBlock("Description"), 1, 0);
        form.Cell(descriptionBox.Stretch(), 1, 1);
        form.Cell(validationText, 2, 0, columnSpan: 2);

        Dialog? createDialog = null;
        var createButton = new Button("Create")
            .Tone(ControlTone.Success)
            .Click(() => _ = CreateSkillFromDialogAsync(createDialog, nameBox, descriptionBox, validationText));
        var cancelButton = new Button("Cancel")
            .Click(() => createDialog?.Close());
        var content = new VStack(
            new Markup(BuildNewSkillTargetHint()) { Wrap = true },
            form,
            new HStack(cancelButton, createButton)
            {
                HorizontalAlignment = Align.End,
                Spacing = 2,
            })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        createDialog = new Dialog()
            .Title("New Skill")
            .BottomRightText(new Markup("[dim]Esc Cancel[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(createDialog, _getBounds(), minWidth: 76, minHeight: 12, widthFactor: 0.48, heightFactor: 0.32);
        createDialog.AddCommand(new Command
        {
            Id = "CodeAlta.Skills.New.Close",
            LabelMarkup = "Cancel",
            DescriptionMarkup = "Close the new skill dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => createDialog.Close(),
        });
        createDialog.Show();
        createDialog.App?.Focus(nameBox);
    }

    private async Task CreateSkillFromDialogAsync(
        Dialog? createDialog,
        TextBox nameBox,
        TextBox descriptionBox,
        TextBlock validationText)
    {
        validationText.Text = string.Empty;
        try
        {
            var result = await _service.CreateSkillAsync(GetSelectedScope(), nameBox.Text, descriptionBox.Text);
            createDialog?.Close();
            await ReloadAsync(result.Name);
            _summaryText = $"[success]Created skill '{AnsiMarkup.Escape(result.Name)}' at {AnsiMarkup.Escape(result.SkillRootPath)}.[/]";
            await _openFileAsync(result.SkillFilePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            validationText.Text = ex.Message;
        }
    }

    private string BuildNewSkillTargetHint()
    {
        var target = GetSelectedScope() == SkillsManagementScope.User
            ? "user CodeAlta skills (`~/.alta/skills/`)"
            : "project CodeAlta skills (`<project>/.alta/skills/`) when a project is selected, otherwise user CodeAlta skills";
        return $"[dim]Creates a scaffolded Agent Skills-compatible `SKILL.md` plus `scripts/`, `references/`, and `assets/` folders under {target}.[/]";
    }

    private async Task ActivateSelectedSkillAsync()
    {
        if (GetSelectedDescriptor() is not { } descriptor)
        {
            return;
        }

        if (!descriptor.IsEnabled)
        {
            _summaryText = "[warning]Enable the selected skill globally and for the project before activating.[/]";
            return;
        }

        if (!descriptor.IsValid || descriptor.IsShadowed)
        {
            _summaryText = "[warning]Select a valid, unshadowed skill before activating.[/]";
            return;
        }

        try
        {
            _summaryText = $"[primary]Activating skill '{AnsiMarkup.Escape(descriptor.Name)}'...[/]";
            await _activateSkillAsync(descriptor.Name, CancellationToken.None);
            _summaryText = $"[success]Activation requested for skill '{AnsiMarkup.Escape(descriptor.Name)}'.[/]";
        }
        catch (Exception ex)
        {
            _summaryText = $"[error]Failed to activate skill '{AnsiMarkup.Escape(descriptor.Name)}': {AnsiMarkup.Escape(ex.Message)}[/]";
        }
    }

    private SkillManagementRowViewModel CreateSkillRow(SkillDescriptor descriptor)
        => new(descriptor, SetSkillEnabledAsync);

    private async Task SetSkillEnabledAsync(SkillManagementRowViewModel row, SkillEnablementScope scope, bool enabled)
    {
        var skillName = row.Descriptor.Name;
        try
        {
            var result = await Task.Run(() => _service.SetSkillEnabled(scope, skillName, enabled));
            await ReloadAsync(skillName);
            _summaryText = BuildEnablementUpdateMarkup(result, enabled ? "enabled" : "disabled", skillName);
        }
        catch (Exception ex)
        {
            await ReloadAsync(skillName);
            _summaryText = $"[error]Failed to update skill enablement: {AnsiMarkup.Escape(ex.Message)}[/]";
        }
    }

    private async Task ApplyBulkEnablementAsync(bool enabled)
    {
        var names = GetShownSkillNames();
        if (names.Count == 0)
        {
            _summaryText = "[warning]No shown skills to update.[/]";
            return;
        }

        try
        {
            var scope = GetSelectedBulkScope();
            var selectedName = GetSelectedDescriptor()?.Name;
            var result = await Task.Run(() => _service.SetSkillsEnabled(scope, names, enabled));
            await ReloadAsync(selectedName);
            _summaryText = BuildEnablementUpdateMarkup(result, enabled ? "enabled" : "disabled", $"{names.Count} shown skill(s)");
        }
        catch (Exception ex)
        {
            _summaryText = $"[error]Failed to update skill enablement: {AnsiMarkup.Escape(ex.Message)}[/]";
        }
    }

    private async Task InvertBulkEnablementAsync()
    {
        var names = GetShownSkillNames();
        if (names.Count == 0)
        {
            _summaryText = "[warning]No shown skills to update.[/]";
            return;
        }

        try
        {
            var scope = GetSelectedBulkScope();
            var selectedName = GetSelectedDescriptor()?.Name;
            var result = await Task.Run(() => _service.InvertSkillsEnabled(scope, names));
            await ReloadAsync(selectedName);
            _summaryText = BuildEnablementUpdateMarkup(result, "inverted", $"{names.Count} shown skill(s)");
        }
        catch (Exception ex)
        {
            _summaryText = $"[error]Failed to update skill enablement: {AnsiMarkup.Escape(ex.Message)}[/]";
        }
    }

    private IReadOnlyList<string> GetShownSkillNames()
        => _rows
            .Select(static row => row.Descriptor.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildEnablementUpdateMarkup(SkillEnablementUpdateResult result, string action, string target)
        => result.TotalChanged == 0
            ? $"[muted]No skill enablement changes were needed for {AnsiMarkup.Escape(target)}.[/]"
            : $"[success]{AnsiMarkup.Escape(action)} {AnsiMarkup.Escape(target)}: {result.GlobalChanged} global, {result.ProjectChanged} project change(s).[/]";

    private SkillDescriptor? GetSelectedDescriptor()
    {
        var index = GetSelectedSkillIndex();
        return (uint)index < (uint)_rows.Count
            ? _rows[index].Descriptor
            : null;
    }

    private SkillsManagementScope GetSelectedScope()
    {
        var index = _scopeSelect.SelectedIndex;
        return (uint)index < (uint)ScopeOptions.Length
            ? ScopeOptions[index].Scope
            : SkillsManagementScope.Combined;
    }

    private SkillEnablementScope GetSelectedBulkScope()
    {
        var index = _bulkScopeSelect.SelectedIndex;
        return (uint)index < (uint)BulkScopeOptions.Length
            ? BulkScopeOptions[index].Scope
            : SkillEnablementScope.Global;
    }

    private int FindSkillIndex(string skillName)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Descriptor.Name, skillName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return _rows.Count > 0 ? 0 : -1;
    }

    private static string BuildDetailMarkdown(SkillDescriptor? descriptor, int relatedFileCount)
    {
        if (descriptor is null)
        {
            return "_No skills were discovered for the selected scope._";
        }

        var builder = new StringBuilder();
        builder.Append("# ")
            .AppendLine(EscapeMarkdownText(descriptor.Name));
        builder.AppendLine();
        builder.AppendLine(EscapeMarkdownText(descriptor.Description));
        builder.AppendLine();

        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        AppendTableRow(builder, "Status", FormatStatus(descriptor));
        AppendTableRow(builder, "Enabled", descriptor.IsEnabled ? "yes" : "no");
        AppendTableRow(builder, "Disabled by", FormatDisabledBy(descriptor));
        AppendTableRow(builder, "Source", FormatSource(descriptor.SourceKind));
        AppendTableRow(builder, "Scope", descriptor.Scope.ToString());
        AppendTableRow(builder, "Trusted for model advertisement", descriptor.IsTrusted ? "yes" : "no");
        AppendTableRow(builder, "Model visible", descriptor.IsModelVisible ? "yes" : "no");
        AppendTableRow(builder, "Related files", relatedFileCount.ToString(CultureInfo.InvariantCulture));
        if (descriptor.IsShadowed)
        {
            AppendMarkdownTableRow(builder, "Shadowed by", Code(descriptor.ShadowedBySkillFilePath));
        }

        builder.AppendLine();
        builder.AppendLine("## Paths");
        builder.AppendLine();
        builder.AppendLine("| Path | Value |");
        builder.AppendLine("| --- | --- |");
        AppendMarkdownTableRow(builder, "SKILL.md", Code(descriptor.SkillFilePath));
        AppendMarkdownTableRow(builder, "Root", Code(descriptor.SkillRootPath));
        AppendMarkdownTableRow(builder, "Source id", Code(descriptor.SourceId));

        if (descriptor.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            builder.AppendLine();
            builder.AppendLine("| Severity | Code | Message |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var diagnostic in descriptor.Diagnostics)
            {
                builder.Append("| ")
                    .Append(EscapeMarkdownTableCell(diagnostic.Severity.ToString()))
                    .Append(" | ")
                    .Append(Code(diagnostic.Code))
                    .Append(" | ")
                    .Append(EscapeMarkdownTableCell(diagnostic.Message))
                    .AppendLine(" |");
            }
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("> No validation diagnostics for this skill.");
        }

        return builder.ToString();
    }

    private static string BuildRelatedFilesHeaderMarkup(int count)
    {
        return count == 0
            ? "[dim]No related files for the selected skill.[/]"
            : $"[dim]{count.ToString(CultureInfo.InvariantCulture)} related file(s): scripts, references, and assets.[/]";
    }

    private static string BuildSummaryMarkup(
        IReadOnlyList<SkillDescriptor> descriptors,
        int shownCount,
        string filterText)
    {
        var valid = descriptors.Count(static descriptor => descriptor.IsValid);
        var invalid = descriptors.Count(static descriptor => !descriptor.IsValid);
        var shadowed = descriptors.Count(static descriptor => descriptor.IsShadowed);
        var disabled = descriptors.Count(static descriptor => !descriptor.IsEnabled);
        var visible = descriptors.Count(static descriptor => descriptor.IsModelVisible);
        var filterSuffix = string.IsNullOrWhiteSpace(filterText)
            ? string.Empty
            : $"   [muted]{shownCount} shown for '{AnsiMarkup.Escape(filterText)}'[/]";
        return $"[primary]{descriptors.Count} discovered[/]   [success]{valid} valid[/]   [warning]{invalid} invalid[/]   [muted]{shadowed} shadowed[/]   [warning]{disabled} disabled[/]   [accent]{visible} model-visible[/]{filterSuffix}";
    }

    private static void ConfigureSkillGridColumns(DataGridControl grid, bool hasSelectedProject)
    {
        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "global",
            Header = new TextBlock("G"),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.GlobalEnabled,
            Width = GridLength.Fixed(3),
            CellActivationMode = DataGridCellActivationMode.DirectActivate,
        });
        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "project",
            Header = new TextBlock("P"),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.ProjectEnabled,
            Width = GridLength.Fixed(3),
            ReadOnly = !hasSelectedProject,
            CellActivationMode = DataGridCellActivationMode.DirectActivate,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "builtin",
            Header = new TextBlock("Built-in"),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.BuiltIn,
            Width = GridLength.Auto,
            ReadOnly = true,
            CellAlignment = TextAlignment.Center,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "name",
            Header = new TextBlock("Skill"),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.Name,
            Width = GridLength.Star(1),
            ReadOnly = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "status",
            Header = new TextBlock("Status"),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.Status,
            Width = GridLength.Auto,
            ReadOnly = true,
        });
    }

    private static Visual Tooltip(Button button, string tooltipText)
        => button.Tooltip(new TextBlock(tooltipText));

    private static void AppendTableRow(StringBuilder builder, string field, string value)
    {
        builder.Append("| ")
            .Append(EscapeMarkdownTableCell(field))
            .Append(" | ")
            .Append(EscapeMarkdownTableCell(value))
            .AppendLine(" |");
    }

    private static void AppendMarkdownTableRow(StringBuilder builder, string field, string markdownValue)
    {
        builder.Append("| ")
            .Append(EscapeMarkdownTableCell(field))
            .Append(" | ")
            .Append(markdownValue)
            .AppendLine(" |");
    }

    private static string Code(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "_none_"
            : $"`{value.Replace("`", "\\`", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)}`";

    private static string EscapeMarkdownText(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string EscapeMarkdownTableCell(string value)
        => EscapeMarkdownText(value)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", "<br/>", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal)
            .Replace("\r", "<br/>", StringComparison.Ordinal);

    private static string FormatStatus(SkillDescriptor descriptor)
    {
        if (!descriptor.IsEnabled)
        {
            return "disabled";
        }

        if (descriptor.IsShadowed)
        {
            return "shadowed";
        }

        return descriptor.IsValid ? "valid" : "invalid";
    }

    private static string FormatDisabledBy(SkillDescriptor descriptor)
    {
        return (descriptor.IsDisabledGlobally, descriptor.IsDisabledForProject) switch
        {
            (true, true) => "global and project config",
            (true, false) => "global config",
            (false, true) => "project config",
            _ => "none",
        };
    }

    private static string FormatSource(SkillSourceKind sourceKind)
    {
        return sourceKind switch
        {
            SkillSourceKind.ProjectAlta => "project .alta/skills",
            SkillSourceKind.ProjectCommon => "project .agents/skills",
            SkillSourceKind.UserAlta => "user ~/.alta/skills",
            SkillSourceKind.UserCommon => "user ~/.agents/skills",
            SkillSourceKind.Plugin => "plugin",
            SkillSourceKind.Builtin => "builtin",
            _ => "temporary",
        };
    }

    private void Close()
    {
        var app = _dialog.App;
        BindingManager.Current.ValueChanged -= OnBindingValueChanged;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private sealed record SkillRelatedFileRow(SkillRelatedFile File)
    {
        public override string ToString()
        {
            var prefix = File.Category + "/";
            var displayPath = File.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? File.RelativePath[prefix.Length..]
                : File.RelativePath;
            return $"{File.Category}/{displayPath}";
        }
    }

    private readonly record struct ScopeOption(SkillsManagementScope Scope, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private readonly record struct BulkScopeOption(SkillEnablementScope Scope, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
