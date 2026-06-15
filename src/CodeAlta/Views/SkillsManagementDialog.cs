using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.App;
using CodeAlta.Catalog.Skills;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
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
        new(SkillsManagementScope.Combined, SR.T("Combined")),
        new(SkillsManagementScope.CurrentProject, SR.T("Current Project")),
        new(SkillsManagementScope.User, SR.T("User")),
    ];

    private static readonly BulkScopeOption[] BulkScopeOptions =
    [
        new(SkillEnablementScope.Global, SR.T("Global")),
        new(SkillEnablementScope.Project, SR.T("Project")),
        new(SkillEnablementScope.Both, SR.T("Both")),
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
    private readonly State<DataGridCell> _currentSkillCell = new(DataGridCell.None);
    private readonly State<int> _selectedRelatedFileIndex = new(-1);
    private readonly State<int> _skillDetailsVersion = new(0);
    private readonly Markup _summaryMarkup;
    private IReadOnlyList<SkillManagementRowViewModel> _allRows = [];
    private IReadOnlyList<SkillManagementRowViewModel> _rows = [];
    private int _skillDocumentRowCount;
    private string _summaryText = $"[dim]{SR.T("Open, activate, and author filesystem skills.")}[/]";

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

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
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
            .Placeholder(SR.T("Filter by name, description, source, or path"))
            .HorizontalAlignment(Align.Stretch);
        _filterBox.TextDocument.Changed += OnFilterTextChanged;

        _skillDocument = new DataGridListDocument<SkillManagementRowViewModel>();
        using (_skillDocument.BeginUpdate())
        {
            _skillDocument
                .AddColumn(new DataGridColumnInfo<bool>("global", SR.T("G"), false, SkillManagementRowViewModel.Accessor.GlobalEnabled))
                .AddColumn(new DataGridColumnInfo<bool>("project", SR.T("P"), !_service.HasSelectedProject, SkillManagementRowViewModel.Accessor.ProjectEnabled))
                .AddColumn(new DataGridColumnInfo<string>("builtin", SR.T("Built-in"), true, SkillManagementRowViewModel.Accessor.BuiltIn))
                .AddColumn(new DataGridColumnInfo<string>("name", SR.T("Skill"), true, SkillManagementRowViewModel.Accessor.Name))
                .AddColumn(new DataGridColumnInfo<string>("status", SR.T("Status"), true, SkillManagementRowViewModel.Accessor.Status));
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
        _skillGrid.BindCurrentCell(_currentSkillCell);
        ConfigureSkillGridColumns(_skillGrid, _service.HasSelectedProject);

        _summaryMarkup = new Markup(() => _summaryText)
        {
            Wrap = true,
        };

        var refreshButton = new Button(SR.T("Refresh"))
            .Tone(ControlTone.Primary)
            .Click(StartReload);
        var newSkillButton = new Button(SR.T("New skill"))
            .Tone(ControlTone.Primary)
            .Click(ShowNewSkillDialog);
        var activateButton = new Button(SR.T("Activate"))
            .Tone(ControlTone.Success)
            .Click(() => _ = ActivateSelectedSkillAsync());
        var openSkillButton = new Button(SR.T("Open SKILL.md"))
            .Click(() => _ = OpenSelectedSkillAsync());
        var openRelatedButton = new Button(SR.T("Open related"))
            .Click(() => _ = OpenSelectedRelatedFileAsync());

        var enableAllButton = new Button(SR.T("Enable"))
            .Tone(ControlTone.Success)
            .Click(() => _ = ApplyBulkEnablementAsync(enabled: true));
        var disableAllButton = new Button(SR.T("Disable"))
            .Tone(ControlTone.Warning)
            .Click(() => _ = ApplyBulkEnablementAsync(enabled: false));
        var invertButton = new Button(SR.T("Invert"))
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
        toolbar.Cell(new TextBlock(SR.T("Scope")) { VerticalAlignment = Align.Center }, 0, 0);
        toolbar.Cell(_scopeSelect, 0, 1);
        toolbar.Cell(new TextBlock(SR.T("Filter")) { VerticalAlignment = Align.Center }, 0, 2);
        toolbar.Cell(_filterBox, 0, 3);
        toolbar.Cell(
            new HStack(
                Tooltip(newSkillButton, SR.T("Create a new filesystem skill template.")),
                Tooltip(activateButton, SR.T("Activate the selected valid, unshadowed skill in the current session.")),
                Tooltip(openSkillButton, SR.T("Open the selected skill's SKILL.md file.")),
                Tooltip(openRelatedButton, SR.T("Open the selected related script, reference, or asset file.")),
                Tooltip(refreshButton, SR.T("Reload discovered skills for the selected scope.")))
            {
                HorizontalAlignment = Align.End,
                Spacing = 1,
            },
            0,
            4);

        var introText = new Markup($"[dim]{SR.T("Browse skills, manage global/project enablement for the shown list, activate enabled skills, or open SKILL.md and related files. G/P are editable enablement columns; Built-in marks built-in skills.")}[/]")
        {
            Wrap = true,
        };

        var detailPane = new Border(new ComputedVisual(BuildSelectedSkillDetailVisual).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch);
        var relatedPane = new DockLayout(
            top: new ComputedVisual(() => new Markup(BuildSelectedSkillRelatedFilesHeaderMarkup()) { Wrap = false }),
            content: new Border(new ComputedVisual(BuildSelectedSkillRelatedFilesVisual).Stretch())
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
            new TabPage(SR.T("Summary"), detailPane),
            new TabPage(SR.T("Related files"), relatedPane))
        {
            AllowTabDragReorder = false,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var bulkActions = new WrapHStack(
            new TextBlock(SR.T("Bulk")) { VerticalAlignment = Align.Center },
            _bulkScopeSelect,
            Tooltip(enableAllButton, SR.T("Enable the currently shown skills for the selected config scope.")),
            Tooltip(disableAllButton, SR.T("Disable the currently shown skills for the selected config scope.")),
            Tooltip(invertButton, SR.T("Invert the currently shown skills for the selected config scope.")))
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
            .Title(SR.T("Skills"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(contentGrid);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 104, minHeight: 24, widthFactor: 0.86, heightFactor: 0.76);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Skills.Manage.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the skills browser."),
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

    private async Task ReloadAsync(string? preferredSkillName = null, bool selectFirst = false)
    {
        _summaryText = $"[primary]{SR.T("Loading skills...")}[/]";
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
                    _summaryText = $"[error]{SR.T("Failed to load skills: {0}", AnsiMarkup.Escape(ex.Message))}[/]";
                    InvalidateSkillDetails();
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
        InvalidateSkillDetails();
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
            InvalidateSkillDetails();
        }
    }

    private int GetSelectedSkillIndex()
    {
        var index = _currentSkillCell.Value.Row;
        return (uint)index < (uint)_rows.Count ? index : -1;
    }

    private void InvalidateSkillDetails()
    {
        _skillDetailsVersion.Value++;
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
        var relatedFiles = GetSelectedRelatedFiles();
        var index = relatedFiles.Count > 0
            ? Math.Clamp(_selectedRelatedFileIndex.Value, 0, relatedFiles.Count - 1)
            : -1;
        if ((uint)index >= (uint)relatedFiles.Count)
        {
            _summaryText = $"[warning]{SR.T("Select a related file before opening.")}[/]";
            return;
        }

        await _openFileAsync(relatedFiles[index].FullPath, CancellationToken.None);
    }

    private void ShowNewSkillDialog()
    {
        var nameBox = new TextBox()
            .Placeholder(SR.T("lowercase-name"));
        var descriptionBox = new TextBox()
            .Placeholder(SR.T("Describe when this skill should be used"))
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
        form.Cell(new TextBlock(SR.T("Name")), 0, 0);
        form.Cell(nameBox.Stretch(), 0, 1);
        form.Cell(new TextBlock(SR.T("Description")), 1, 0);
        form.Cell(descriptionBox.Stretch(), 1, 1);
        form.Cell(validationText, 2, 0, columnSpan: 2);

        Dialog? createDialog = null;
        var createButton = new Button(SR.T("Create"))
            .Tone(ControlTone.Success)
            .Click(() => _ = CreateSkillFromDialogAsync(createDialog, nameBox, descriptionBox, validationText));
        var cancelButton = new Button(SR.T("Cancel"))
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
            .Title(SR.T("New Skill"))
            .BottomRightText(new Markup($"[dim]{SR.T("Esc Cancel")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(createDialog, _getBounds(), minWidth: 76, minHeight: 12, widthFactor: 0.48, heightFactor: 0.32);
        createDialog.AddCommand(new Command
        {
            Id = "CodeAlta.Skills.New.Close",
            LabelMarkup = SR.T("Cancel"),
            DescriptionMarkup = SR.T("Close the new skill dialog."),
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
            _summaryText = $"[success]{SR.T("Created skill '{0}' at {1}.", AnsiMarkup.Escape(result.Name), AnsiMarkup.Escape(result.SkillRootPath))}[/]";
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
            ? SR.T("user CodeAlta skills (`~/.alta/skills/`)")
            : SR.T("project CodeAlta skills (`<project>/.alta/skills/`) when a project is selected, otherwise user CodeAlta skills");
        return $"[dim]{SR.T("Creates a scaffolded Agent Skills-compatible `SKILL.md` plus `scripts/`, `references/`, and `assets/` folders under {0}.", target)}[/]";
    }

    private async Task ActivateSelectedSkillAsync()
    {
        if (GetSelectedDescriptor() is not { } descriptor)
        {
            return;
        }

        if (!descriptor.IsEnabled)
        {
            _summaryText = $"[warning]{SR.T("Enable the selected skill globally and for the project before activating.")}[/]";
            return;
        }

        if (!descriptor.IsValid || descriptor.IsShadowed)
        {
            _summaryText = $"[warning]{SR.T("Select a valid, unshadowed skill before activating.")}[/]";
            return;
        }

        try
        {
            _summaryText = $"[primary]{SR.T("Activating skill '{0}'...", AnsiMarkup.Escape(descriptor.Name))}[/]";
            await _activateSkillAsync(descriptor.Name, CancellationToken.None);
            _summaryText = $"[success]{SR.T("Activation requested for skill '{0}'.", AnsiMarkup.Escape(descriptor.Name))}[/]";
        }
        catch (Exception ex)
        {
            _summaryText = $"[error]{SR.T("Failed to activate skill '{0}': {1}", AnsiMarkup.Escape(descriptor.Name), AnsiMarkup.Escape(ex.Message))}[/]";
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
            _summaryText = BuildEnablementUpdateMarkup(result, enabled ? SR.T("enabled") : SR.T("disabled"), skillName);
        }
        catch (Exception ex)
        {
            await ReloadAsync(skillName);
            _summaryText = $"[error]{SR.T("Failed to update skill enablement: {0}", AnsiMarkup.Escape(ex.Message))}[/]";
        }
    }

    private async Task ApplyBulkEnablementAsync(bool enabled)
    {
        var names = GetShownSkillNames();
        if (names.Count == 0)
        {
            _summaryText = $"[warning]{SR.T("No shown skills to update.")}[/]";
            return;
        }

        try
        {
            var scope = GetSelectedBulkScope();
            var selectedName = GetSelectedDescriptor()?.Name;
            var result = await Task.Run(() => _service.SetSkillsEnabled(scope, names, enabled));
            await ReloadAsync(selectedName);
            _summaryText = BuildEnablementUpdateMarkup(result, enabled ? SR.T("enabled") : SR.T("disabled"), SR.T("{0} shown skill(s)", names.Count));
        }
        catch (Exception ex)
        {
            _summaryText = $"[error]{SR.T("Failed to update skill enablement: {0}", AnsiMarkup.Escape(ex.Message))}[/]";
        }
    }

    private async Task InvertBulkEnablementAsync()
    {
        var names = GetShownSkillNames();
        if (names.Count == 0)
        {
            _summaryText = $"[warning]{SR.T("No shown skills to update.")}[/]";
            return;
        }

        try
        {
            var scope = GetSelectedBulkScope();
            var selectedName = GetSelectedDescriptor()?.Name;
            var result = await Task.Run(() => _service.InvertSkillsEnabled(scope, names));
            await ReloadAsync(selectedName);
            _summaryText = BuildEnablementUpdateMarkup(result, SR.T("inverted"), SR.T("{0} shown skill(s)", names.Count));
        }
        catch (Exception ex)
        {
            _summaryText = $"[error]{SR.T("Failed to update skill enablement: {0}", AnsiMarkup.Escape(ex.Message))}[/]";
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
            ? $"[muted]{SR.T("No skill enablement changes were needed for {0}.", AnsiMarkup.Escape(target))}[/]"
            : $"[success]{SR.T("{0} {1}: {2} global, {3} project change(s).", AnsiMarkup.Escape(action), AnsiMarkup.Escape(target), result.GlobalChanged, result.ProjectChanged)}[/]";

    private SkillDescriptor? GetSelectedDescriptor()
    {
        var index = GetSelectedSkillIndex();
        return (uint)index < (uint)_rows.Count
            ? _rows[index].Descriptor
            : null;
    }

    private Visual BuildSelectedSkillDetailVisual()
    {
        _ = _skillDetailsVersion.Value;
        var descriptor = GetSelectedDescriptor();
        var relatedFileCount = descriptor is null ? 0 : GetSelectedRelatedFiles(descriptor).Count;
        return new MarkdownControl(BuildDetailMarkdown(descriptor, relatedFileCount))
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
    }

    private Visual BuildSelectedSkillRelatedFilesVisual()
    {
        var rows = GetSelectedRelatedFiles()
            .Select(static file => new SkillRelatedFileRow(file))
            .ToArray();
        var list = new ListBox<SkillRelatedFileRow>(rows, rows.Length > 0 ? 0 : -1)
            .MinHeight(4)
            .Stretch();
        list.BindSelectedIndex(_selectedRelatedFileIndex);
        return new ScrollViewer(list).Stretch();
    }

    private string BuildSelectedSkillRelatedFilesHeaderMarkup()
    {
        _ = _skillDetailsVersion.Value;
        return GetSelectedDescriptor() is { } descriptor
            ? BuildRelatedFilesHeaderMarkup(GetSelectedRelatedFiles(descriptor).Count)
            : $"[dim]{SR.T("Select a skill to inspect related files.")}[/]";
    }

    private IReadOnlyList<SkillRelatedFile> GetSelectedRelatedFiles()
    {
        _ = _skillDetailsVersion.Value;
        return GetSelectedDescriptor() is { } descriptor
            ? GetSelectedRelatedFiles(descriptor)
            : [];
    }

    private IReadOnlyList<SkillRelatedFile> GetSelectedRelatedFiles(SkillDescriptor descriptor)
    {
        _ = _skillDetailsVersion.Value;
        return _service.ListRelatedFiles(descriptor);
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
            return "_" + EscapeMarkdownText(SR.T("No skills were discovered for the selected scope.")) + "_";
        }

        var builder = new StringBuilder();
        builder.Append("# ")
            .AppendLine(EscapeMarkdownText(descriptor.Name));
        builder.AppendLine();
        builder.AppendLine(EscapeMarkdownText(descriptor.Description));
        builder.AppendLine();

        builder.Append("## ").AppendLine(EscapeMarkdownText(SR.T("Summary")));
        builder.AppendLine();
        builder.Append("| ").Append(EscapeMarkdownTableCell(SR.T("Field"))).Append(" | ").Append(EscapeMarkdownTableCell(SR.T("Value"))).AppendLine(" |");
        builder.AppendLine("| --- | --- |");
        AppendTableRow(builder, SR.T("Status"), FormatStatus(descriptor));
        AppendTableRow(builder, SR.T("Enabled"), descriptor.IsEnabled ? SR.T("yes") : SR.T("no"));
        AppendTableRow(builder, SR.T("Disabled by"), FormatDisabledBy(descriptor));
        AppendTableRow(builder, SR.T("Source"), FormatSource(descriptor.SourceKind));
        AppendTableRow(builder, SR.T("Scope"), descriptor.Scope.ToString());
        AppendTableRow(builder, SR.T("Trusted for model advertisement"), descriptor.IsTrusted ? SR.T("yes") : SR.T("no"));
        AppendTableRow(builder, SR.T("Model visible"), descriptor.IsModelVisible ? SR.T("yes") : SR.T("no"));
        AppendTableRow(builder, SR.T("Related files"), relatedFileCount.ToString(CultureInfo.InvariantCulture));
        if (descriptor.IsShadowed)
        {
            AppendMarkdownTableRow(builder, SR.T("Shadowed by"), Code(descriptor.ShadowedBySkillFilePath));
        }

        builder.AppendLine();
        builder.Append("## ").AppendLine(EscapeMarkdownText(SR.T("Paths")));
        builder.AppendLine();
        builder.Append("| ").Append(EscapeMarkdownTableCell(SR.T("Path"))).Append(" | ").Append(EscapeMarkdownTableCell(SR.T("Value"))).AppendLine(" |");
        builder.AppendLine("| --- | --- |");
        AppendMarkdownTableRow(builder, "SKILL.md", Code(descriptor.SkillFilePath));
        AppendMarkdownTableRow(builder, SR.T("Root"), Code(descriptor.SkillRootPath));
        AppendMarkdownTableRow(builder, SR.T("Source id"), Code(descriptor.SourceId));

        if (descriptor.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.Append("## ").AppendLine(EscapeMarkdownText(SR.T("Diagnostics")));
            builder.AppendLine();
            builder.Append("| ").Append(EscapeMarkdownTableCell(SR.T("Severity"))).Append(" | ").Append(EscapeMarkdownTableCell(SR.T("Code"))).Append(" | ").Append(EscapeMarkdownTableCell(SR.T("Message"))).AppendLine(" |");
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
            builder.Append("> ").AppendLine(EscapeMarkdownText(SR.T("No validation diagnostics for this skill.")));
        }

        return builder.ToString();
    }

    private static string BuildRelatedFilesHeaderMarkup(int count)
    {
        return count == 0
            ? $"[dim]{SR.T("No related files for the selected skill.")}[/]"
            : $"[dim]{SR.T("{0} related file(s): scripts, references, and assets.", count.ToString(CultureInfo.InvariantCulture))}[/]";
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
            : $"   [muted]{SR.T("{0} shown for '{1}'", shownCount, AnsiMarkup.Escape(filterText))}[/]";
        return $"[primary]{SR.T("{0} discovered", descriptors.Count)}[/]   [success]{SR.T("{0} valid", valid)}[/]   [warning]{SR.T("{0} invalid", invalid)}[/]   [muted]{SR.T("{0} shadowed", shadowed)}[/]   [warning]{SR.T("{0} disabled", disabled)}[/]   [accent]{SR.T("{0} model-visible", visible)}[/]{filterSuffix}";
    }

    private static void ConfigureSkillGridColumns(DataGridControl grid, bool hasSelectedProject)
    {
        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "global",
            Header = new TextBlock(SR.T("G")),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.GlobalEnabled,
            Width = GridLength.Fixed(3),
            CellActivationMode = DataGridCellActivationMode.DirectActivate,
        });
        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "project",
            Header = new TextBlock(SR.T("P")),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.ProjectEnabled,
            Width = GridLength.Fixed(3),
            ReadOnly = !hasSelectedProject,
            CellActivationMode = DataGridCellActivationMode.DirectActivate,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "builtin",
            Header = new TextBlock(SR.T("Built-in")),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.BuiltIn,
            Width = GridLength.Auto,
            ReadOnly = true,
            CellAlignment = TextAlignment.Center,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "name",
            Header = new TextBlock(SR.T("Skill")),
            TypedValueAccessor = SkillManagementRowViewModel.Accessor.Name,
            Width = GridLength.Star(1),
            ReadOnly = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "status",
            Header = new TextBlock(SR.T("Status")),
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
            ? "_" + EscapeMarkdownText(SR.T("none")) + "_"
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
            return SR.T("disabled");
        }

        if (descriptor.IsShadowed)
        {
            return SR.T("shadowed");
        }

        return descriptor.IsValid ? SR.T("valid") : SR.T("invalid");
    }

    private static string FormatDisabledBy(SkillDescriptor descriptor)
    {
        return (descriptor.IsDisabledGlobally, descriptor.IsDisabledForProject) switch
        {
            (true, true) => SR.T("global and project config"),
            (true, false) => SR.T("global config"),
            (false, true) => SR.T("project config"),
            _ => SR.T("none"),
        };
    }

    private static string FormatSource(SkillSourceKind sourceKind)
    {
        return sourceKind switch
        {
            SkillSourceKind.ProjectAlta => SR.T("project .alta/skills"),
            SkillSourceKind.ProjectCommon => SR.T("project .agents/skills"),
            SkillSourceKind.UserAlta => SR.T("user ~/.alta/skills"),
            SkillSourceKind.UserCommon => SR.T("user ~/.agents/skills"),
            SkillSourceKind.Plugin => SR.T("plugin"),
            SkillSourceKind.Builtin => SR.T("builtin"),
            _ => SR.T("temporary"),
        };
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
