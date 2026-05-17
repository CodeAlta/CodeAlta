using System.Globalization;
using System.Text;
using CodeAlta.App;
using CodeAlta.Catalog.Skills;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Collections;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
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

    private readonly SkillsManagementService _service;
    private readonly Func<string, CancellationToken, Task> _openFileAsync;
    private readonly Func<string, CancellationToken, Task> _activateSkillAsync;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly Select<ScopeOption> _scopeSelect;
    private readonly TextBox _filterBox;
    private readonly ListBox<SkillRow> _skillList;
    private readonly BindableList<SkillRow> _skillItems;
    private readonly State<int> _selectedSkillIndex = new(-1);
    private readonly ListBox<SkillRelatedFileRow> _relatedFileList;
    private readonly BindableList<SkillRelatedFileRow> _relatedFileItems;
    private readonly State<int> _selectedRelatedFileIndex = new(-1);
    private readonly Markup _summaryMarkup;
    private readonly MarkdownControl _detailMarkdown;
    private IReadOnlyList<SkillRow> _allRows = [];
    private IReadOnlyList<SkillRow> _rows = [];
    private string _summaryText = "[dim]Open, activate, and author filesystem skills.[/]";
    private string? _relatedFilesSelectionKey;

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

        _filterBox = new TextBox()
            .Placeholder("Filter by name, description, source, or path")
            .HorizontalAlignment(Align.Stretch);
        _filterBox.TextDocument.Changed += OnFilterTextChanged;

        _skillList = new ListBox<SkillRow>()
            .MinWidth(38)
            .Stretch();
        _skillItems = _skillList.Items;
        _skillList.SelectedIndex(_selectedSkillIndex.Bind.Value);
        _skillList.ItemTemplate = new DataTemplate<SkillRow>(
            static (DataTemplateValue<SkillRow> value, in DataTemplateContext _) => new TextBlock(value.GetValue().ToString()),
            null);

        _relatedFileList = new ListBox<SkillRelatedFileRow>()
            .MinHeight(4)
            .Stretch();
        _relatedFileItems = _relatedFileList.Items;
        _relatedFileList.SelectedIndex(_selectedRelatedFileIndex.Bind.Value);

        _summaryMarkup = new Markup(() => _summaryText)
        {
            Wrap = true,
        };

        _detailMarkdown = new MarkdownControl(BuildDetailMarkdown)
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
                new ColumnDefinition { Width = GridLength.Star(1) });
        toolbar.Cell(new TextBlock("Scope") { VerticalAlignment = Align.Center }, 0, 0);
        toolbar.Cell(_scopeSelect, 0, 1);
        toolbar.Cell(new TextBlock("Filter") { VerticalAlignment = Align.Center }, 0, 2);
        toolbar.Cell(_filterBox, 0, 3);
        toolbar.Cell(
            new HStack(newSkillButton, activateButton, openSkillButton, openRelatedButton, refreshButton)
            {
                HorizontalAlignment = Align.End,
                Spacing = 1,
            },
            0,
            4);

        var introText = new Markup("[dim]Browse discovered filesystem skills, validation state, source precedence, and provenance. New skill creates a template in project .alta/skills when a project is selected, otherwise user .alta/skills; Open edits SKILL.md or related scripts/references/assets.[/]")
        {
            Wrap = true,
        };

        var detailPane = new Border(_detailMarkdown.Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch);
        var relatedPane = new VStack(
            new Markup("[dim]Related files (scripts, references, assets)[/]") { Wrap = false },
            new Border(new ScrollViewer(_relatedFileList).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 0,
        };
        var rightPane = new VSplitter(detailPane, relatedPane)
        {
            Ratio = 0.72,
            MinFirst = 8,
            MinSecond = 5,
        };

        var leftPane = new VStack(
            new TextBlock("Skills"),
            new Border(new ScrollViewer(_skillList).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 0,
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
    }

    private void StartReload()
        => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        _summaryText = "[primary]Loading skills...[/]";
        var scope = GetSelectedScope();
        try
        {
            var descriptors = await Task.Run(() => _service.LoadAsync(scope));
            await _dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    _allRows = descriptors
                        .OrderBy(static descriptor => descriptor.Precedence)
                        .ThenBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(static descriptor => new SkillRow(descriptor))
                        .ToArray();

                    ApplyFilter(selectFirst: true);
                });
        }
        catch (Exception ex)
        {
            await _dialog.Dispatcher.InvokeAsync(
                () =>
                {
                    _allRows = [];
                    _rows = [];
                    _skillItems.Clear();
                    _selectedSkillIndex.Value = -1;
                    _summaryText = $"[error]Failed to load skills: {AnsiMarkup.Escape(ex.Message)}[/]";
                    EnsureRelatedFilesForSelection();
                });
        }
    }

    private void OnFilterTextChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyFilter(selectFirst: true);
    }

    private void ApplyFilter(bool selectFirst)
    {
        var filterText = (_filterBox.Text ?? string.Empty).Trim();
        _rows = string.IsNullOrWhiteSpace(filterText)
            ? _allRows
            : _allRows
                .Where(row => row.Matches(filterText))
                .ToArray();

        var selectedName = GetSelectedDescriptor()?.Name;
        _skillItems.Clear();
        foreach (var row in _rows)
        {
            _skillItems.Add(row);
        }

        if (selectFirst)
        {
            _selectedSkillIndex.Value = _rows.Count > 0 ? 0 : -1;
        }
        else if (!string.IsNullOrWhiteSpace(selectedName))
        {
            _selectedSkillIndex.Value = FindSkillIndex(selectedName);
        }
        else if (_selectedSkillIndex.Value >= _rows.Count)
        {
            _selectedSkillIndex.Value = _rows.Count > 0 ? _rows.Count - 1 : -1;
        }

        _summaryText = BuildSummaryMarkup(_allRows.Select(static row => row.Descriptor).ToArray(), _rows.Count, filterText);
        EnsureRelatedFilesForSelection();
    }

    private void EnsureRelatedFilesForSelection()
    {
        var descriptor = GetSelectedDescriptor();
        var selectionKey = descriptor?.SkillFilePath;
        if (string.Equals(_relatedFilesSelectionKey, selectionKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _relatedFilesSelectionKey = selectionKey;
        _relatedFileItems.Clear();
        if (descriptor is null)
        {
            _selectedRelatedFileIndex.Value = -1;
            return;
        }

        foreach (var file in _service.ListRelatedFiles(descriptor))
        {
            _relatedFileItems.Add(new SkillRelatedFileRow(file));
        }

        _selectedRelatedFileIndex.Value = _relatedFileItems.Count > 0 ? 0 : -1;
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
        EnsureRelatedFilesForSelection();
        var index = _selectedRelatedFileIndex.Value;
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
            await ReloadAsync();
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

    private SkillDescriptor? GetSelectedDescriptor()
    {
        var index = _selectedSkillIndex.Value;
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

    private string BuildDetailMarkdown()
    {
        EnsureRelatedFilesForSelection();
        var descriptor = GetSelectedDescriptor();
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
        AppendTableRow(builder, "Source", FormatSource(descriptor.SourceKind));
        AppendTableRow(builder, "Scope", descriptor.Scope.ToString());
        AppendTableRow(builder, "Trusted for model advertisement", descriptor.IsTrusted ? "yes" : "no");
        AppendTableRow(builder, "Model visible", descriptor.IsModelVisible ? "yes" : "no");
        AppendTableRow(builder, "Related files", _relatedFileItems.Count.ToString(CultureInfo.InvariantCulture));
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

    private static string BuildSummaryMarkup(
        IReadOnlyList<SkillDescriptor> descriptors,
        int shownCount,
        string filterText)
    {
        var valid = descriptors.Count(static descriptor => descriptor.IsValid);
        var invalid = descriptors.Count(static descriptor => !descriptor.IsValid);
        var shadowed = descriptors.Count(static descriptor => descriptor.IsShadowed);
        var visible = descriptors.Count(static descriptor => descriptor.IsModelVisible);
        var filterSuffix = string.IsNullOrWhiteSpace(filterText)
            ? string.Empty
            : $"   [muted]{shownCount} shown for '{AnsiMarkup.Escape(filterText)}'[/]";
        return $"[primary]{descriptors.Count} discovered[/]   [success]{valid} valid[/]   [warning]{invalid} invalid[/]   [muted]{shadowed} shadowed[/]   [accent]{visible} model-visible[/]{filterSuffix}";
    }

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
        if (descriptor.IsShadowed)
        {
            return "shadowed";
        }

        return descriptor.IsValid ? "valid" : "invalid";
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
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private sealed record SkillRow(SkillDescriptor Descriptor)
    {
        public bool Matches(string filterText)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filterText);

            return Descriptor.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                   Descriptor.Description.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                   Descriptor.SkillFilePath.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                   Descriptor.SkillRootPath.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                   Descriptor.SourceKind.ToString().Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                   Descriptor.Scope.ToString().Contains(filterText, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            var status = Descriptor.IsShadowed
                ? "shadowed"
                : Descriptor.IsValid ? "valid" : "invalid";
            return $"{Descriptor.Name} · {status} · {FormatSource(Descriptor.SourceKind)}";
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
}
