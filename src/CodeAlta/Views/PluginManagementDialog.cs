using System.Text;
using CodeAlta.Catalog;
using CodeAlta.App;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
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
using UiCommand = XenoAtom.Terminal.UI.Commands.Command;

namespace CodeAlta.Views;

internal sealed class PluginManagementDialog
{
    private readonly PluginManagementService _service;
    private readonly Func<string, CancellationToken, Task> _openFileAsync;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;
    private readonly ListBox<PluginManagementRow> _pluginList;
    private readonly BindableList<PluginManagementRow> _plugins;
    private readonly State<int> _selectedPluginIndex = new(-1);
    private readonly Markup _summaryMarkup;
    private readonly Markup _statusMarkup;
    private readonly Visual _detailHost;
    private string _summaryText = $"[dim]{SR.T("Plugin configuration has not been loaded yet.")}[/]";
    private string _statusText = $"[dim]{SR.T("Use Refresh to reload plugin discovery and configuration.")}[/]";

    public PluginManagementDialog(
        PluginManagementService service,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(openFileAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        _service = service;
        _openFileAsync = openFileAsync;
        _getFocusTarget = getFocusTarget;

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(Close);

        _pluginList = new ListBox<PluginManagementRow>()
            .MinWidth(34)
            .Stretch();
        _plugins = _pluginList.Items;
        _pluginList.SelectedIndex(_selectedPluginIndex.Bind.Value);
        _pluginList.ItemTemplate = new DataTemplate<PluginManagementRow>(
            (DataTemplateValue<PluginManagementRow> value, in DataTemplateContext context) => BuildPluginListItem(value.GetValue(), context.Index),
            null);

        _summaryMarkup = new Markup(() => _summaryText)
        {
            Wrap = true,
        };
        _statusMarkup = new Markup(() => _statusText)
        {
            Wrap = true,
        };
        _detailHost = new ComputedVisual(
            () =>
            {
                var index = _selectedPluginIndex.Value;
                return index >= 0 && index < _plugins.Count
                    ? BuildDetailPane(_plugins[index])
                    : BuildEmptyState();
            });

        var refreshButton = new Button($"{TerminalIcons.MdRefresh} {SR.T("Refresh")}")
            .Tone(ControlTone.Primary)
            .Click(() => Reload(null));

        var header = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) },
                new ColumnDefinition { Width = GridLength.Auto });
        header.Cell(_summaryMarkup, 0, 0);
        header.Cell(refreshButton, 0, 1);

        var intro = new Markup($"[dim]{SR.T("Source plugins are trusted code: build and load operations can execute local plugin or package build logic. Use --no-plugins, --plugin-safe-mode, or CODEALTA_DISABLE_PLUGINS=1 if a plugin breaks startup.")}[/]")
        {
            Wrap = true,
        };

        var leftPane = new VStack(
            new Group(SR.T("Plugins"))
                .Style(GroupStyle.Rounded)
                .Content(_pluginList.Stretch())
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            new Markup($"[dim]{SR.T("Each row shows kind, scope, status, and a short description when available.")}[/]") { Wrap = true })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        var rightPane = new Group(SR.T("Plugin Details"))
            .Style(GroupStyle.Rounded)
            .Content(new ScrollViewer(_detailHost).Stretch())
            .Padding(1)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        var splitter = new HSplitter(leftPane, rightPane)
        {
            Ratio = 0.32,
            MinFirst = 30,
            MinSecond = 56,
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
            .Title(SR.T("Plugins"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 110, minHeight: 28, widthFactor: 0.84, heightFactor: 0.78);
        _dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Plugins.Manage.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the plugins dialog."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
    {
        _dialog.Show();
        Reload(null);
        _dialog.App?.Focus(_pluginList);
    }

    private void Reload(string? preferredKey)
    {
        var selectedKey = preferredKey ?? GetSelectedRow()?.Entry.Key;
        try
        {
            var snapshot = _service.LoadSnapshot();
            _plugins.Clear();
            _plugins.AddRange(snapshot.Entries.Select(static entry => new PluginManagementRow(entry)));
            _summaryText = BuildSummaryMarkup(snapshot);
            _statusText = snapshot.Entries.Count == 0
                ? $"[warning]{SR.T("No built-in or source plugins were discovered for the current scope.")}[/]"
                : $"[dim]{SR.T("Select a plugin to inspect diagnostics, edit enablement, or open plugin files.")}[/]";

            var selectedIndex = selectedKey is null ? -1 : FindPluginIndex(selectedKey);
            if (selectedIndex < 0 && _plugins.Count > 0)
            {
                selectedIndex = 0;
            }

            SetSelectedPluginIndex(selectedIndex);
        }
        catch (Exception ex)
        {
            _plugins.Clear();
            SetSelectedPluginIndex(-1);
            _summaryText = $"[error]{SR.T("Failed to load plugin management data.")}[/]";
            _statusText = $"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]";
        }
    }

    private Visual BuildDetailPane(PluginManagementRow row)
    {
        var entry = row.Entry;
        var enablement = new HStack(
            new CheckBox(SR.T("Enabled")).IsChecked(row.EnabledState),
            new Button(SR.T("Apply"))
                .Tone(ControlTone.Success)
                .IsEnabled(() => row.EnabledState.Value != entry.Enabled)
                .Click(() => ApplyEnablement(row)),
            new Markup(() => row.EnabledState.Value == entry.Enabled
                ? $"[dim]{SR.T("Saved")}[/]"
                : $"[warning]{SR.T("Unsaved enablement change")}[/]") { Wrap = false })
        {
            Spacing = 1,
        };

        var sourceButton = new Button($"{TerminalIcons.MdFileDocumentEditOutline} {SR.T("Open plugin.cs")}")
            .IsEnabled(!string.IsNullOrWhiteSpace(entry.SourcePath))
            .Click(() => _ = OpenFileAsync(entry.SourcePath, SR.T("plugin source")));
        var readmeButton = new Button($"{TerminalIcons.MdFileDocumentOutline} {SR.T("Open README")}")
            .IsEnabled(!string.IsNullOrWhiteSpace(entry.ReadmePath))
            .Click(() => _ = OpenFileAsync(entry.ReadmePath, SR.T("plugin README")));
        var rebuildButton = new Button($"{TerminalIcons.MdCogRefreshOutline} {SR.T("Rebuild")}")
            .IsEnabled(false);
        var reloadButton = new Button($"{TerminalIcons.MdReload} {SR.T("Reload")}")
            .IsEnabled(false);
        var cleanButton = new Button($"{TerminalIcons.MdDeleteSweepOutline} {SR.T("Clean")}")
            .IsEnabled(false);

        var actionPane = new VStack(
            new HStack(sourceButton, readmeButton, rebuildButton, reloadButton, cleanButton)
            {
                Spacing = 1,
            },
            new Markup($"[dim]{SR.T("Open actions are available now. Rebuild, Reload, and Clean are shown in-place and will be enabled when runtime action handlers are connected.")}[/]") { Wrap = true })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };

        return new VStack(
            new Markup(BuildSelectedTitleMarkup(entry)) { Wrap = true },
            new Markup(BuildSelectedDescriptionMarkup(entry)) { Wrap = true },
            CreateSection(SR.T("Enablement"), enablement),
            CreateSection(SR.T("Actions"), actionPane),
            CreateSection(SR.T("Properties"), BuildPropertiesGrid(entry)),
            CreateSection(SR.T("Diagnostics"), new Markup(BuildDiagnosticsMarkup(entry)) { Wrap = true }),
            CreateSection(SR.T("Contributions"), new Markup(BuildContributionsMarkup(entry)) { Wrap = true }))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Spacing = 1,
        };
    }

    private void ApplyEnablement(PluginManagementRow row)
    {
        var enabled = row.EnabledState.Value;
        if (enabled == row.Entry.Enabled)
        {
            _statusText = $"[dim]{SR.T("No plugin enablement changes to save.")}[/]";
            return;
        }

        try
        {
            _service.SetPluginEnabled(row.Entry, enabled);
            var statusText = enabled
                ? $"[success]{SR.T("Plugin enablement saved. Restart or reload plugins to apply runtime changes.")}[/]"
                : $"[success]{SR.T("Plugin disablement saved. Restart or reload plugins to unload active runtime contributions.")}[/]";
            Reload(row.Entry.Key);
            _statusText = statusText;
        }
        catch (Exception ex)
        {
            row.EnabledState.Value = row.Entry.Enabled;
            _statusText = $"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]";
        }
    }

    private async Task OpenFileAsync(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _statusText = $"[warning]{SR.T("This plugin does not have a {0} path.", AnsiMarkup.Escape(label))}[/]";
            return;
        }

        try
        {
            await _openFileAsync(path, CancellationToken.None);
            _statusText = $"[success]{SR.T("Opened {0}.", AnsiMarkup.Escape(label))}[/]";
        }
        catch (Exception ex)
        {
            _statusText = $"[error]{SR.T("Failed to open {0}:", AnsiMarkup.Escape(label))}[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
        }
    }

    private static Visual CreateSection(string title, Visual content)
        => new Group(title)
            .Style(GroupStyle.Rounded)
            .Content(content)
            .Padding(new Thickness(1, 0, 1, 0))
            .HorizontalAlignment(Align.Stretch);

    private Visual BuildPluginListItem(PluginManagementRow row, int index)
        => new Markup(() => BuildPluginListItemMarkup(row.Entry, _selectedPluginIndex.Value == index))
        {
            Wrap = false,
        };

    private static string BuildPluginListItemMarkup(PluginManagementEntry entry, bool selected)
    {
        var (tone, icon) = GetStatusToneAndIcon(entry.State);
        var description = GetDescription(entry);
        var hint = $"{FormatKind(entry)} · {FormatStateText(entry.State)}";
        if (!string.IsNullOrWhiteSpace(description))
        {
            hint += $" · {description}";
        }

        var hintMarkup = selected
            ? AnsiMarkup.Escape(hint)
            : $"[dim]{AnsiMarkup.Escape(hint)}[/]";
        return $"[{tone}]{icon} {AnsiMarkup.Escape(entry.DisplayName)}[/] {hintMarkup}";
    }

    private static string BuildSelectedTitleMarkup(PluginManagementEntry entry)
    {
        var (tone, icon) = GetStatusToneAndIcon(entry.State);
        return $"[{tone}]{icon} {AnsiMarkup.Escape(entry.DisplayName)}[/] [dim]· {AnsiMarkup.Escape(FormatKind(entry))} · {FormatStateText(entry.State)}[/]";
    }

    private static string BuildSelectedDescriptionMarkup(PluginManagementEntry entry)
    {
        var description = GetDescription(entry);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = SR.T("No description was discovered for this plugin.");
        }

        return AnsiMarkup.Escape(description);
    }

    private static Visual BuildPropertiesGrid(PluginManagementEntry entry)
    {
        var rows = new List<(string Label, string? Value)>
        {
            (SR.T("Key"), entry.Key),
            (SR.T("Plugin Id"), entry.PluginId),
            (SR.T("Kind"), entry.LoadUnitKind.ToString()),
            (SR.T("Scope"), entry.Scope.ToString()),
            (SR.T("Configured"), entry.Enabled ? SR.T("Enabled") : SR.T("Disabled")),
            (SR.T("Source"), entry.SourcePath),
            ("README", entry.ReadmePath),
            (SR.T("Output"), entry.OutputAssemblyPath),
            (SR.T("Build"), entry.LastBuildSummary?.ToString()),
            (SR.T("Reason"), TryGetMetadata(entry, "Reason")),
        };

        var grid = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                RowGap = 0,
            }
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });

        var rowIndex = 0;
        foreach (var (label, value) in rows.Where(static row => !string.IsNullOrWhiteSpace(row.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Cell(new TextBlock(label) { VerticalAlignment = Align.Start }, rowIndex, 0);
            grid.Cell(new TextBlock(value!) { Wrap = true }, rowIndex, 1);
            rowIndex++;
        }

        if (rowIndex == 0)
        {
            return new TextBlock(SR.T("No properties available.")) { Wrap = true };
        }

        return grid;
    }

    private static string BuildDiagnosticsMarkup(PluginManagementEntry entry)
    {
        if (entry.Diagnostics.Count == 0)
        {
            return $"[success]{SR.T("No diagnostics.")}[/]";
        }

        var builder = new StringBuilder();
        foreach (var diagnostic in entry.Diagnostics)
        {
            var tone = diagnostic.Severity >= PluginDiagnosticSeverity.Error
                ? "error"
                : diagnostic.Severity >= PluginDiagnosticSeverity.Warning
                    ? "warning"
                    : "primary";
            builder.Append("[").Append(tone).Append(']')
                .Append(diagnostic.Severity)
                .Append("[/] ")
                .Append(AnsiMarkup.Escape(diagnostic.Message))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildContributionsMarkup(PluginManagementEntry entry)
    {
        if (entry.Contributions.Count == 0)
        {
            return entry.Enabled
                ? $"[dim]{SR.T("No active contributions were reported for this snapshot.")}[/]"
                : $"[dim]{SR.T("Disabled plugins do not contribute runtime features.")}[/]";
        }

        var builder = new StringBuilder();
        foreach (var contribution in entry.Contributions)
        {
            builder.Append("- ")
                .Append(AnsiMarkup.Escape(contribution.Handle.Point.ToString()))
                .Append(": ")
                .Append(AnsiMarkup.Escape(contribution.Handle.NaturalName ?? contribution.ContributionTypeName))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildSummaryMarkup(PluginManagementSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("[bold]").Append(SR.T("Status")).Append(":[/] ");
        builder.Append(snapshot.SafeMode ? $"[warning]{SR.T("safe mode enabled")}[/]" : $"[success]{SR.T("plugins enabled by policy")}[/]");
        if (!string.IsNullOrWhiteSpace(snapshot.ProjectPath))
        {
            builder.Append("  [bold]").Append(SR.T("Project root")).Append(":[/] ").Append(AnsiMarkup.Escape(snapshot.ProjectPath));
        }

        builder.Append("  [bold]").Append(SR.T("Plugins")).Append(":[/] ").Append(snapshot.Entries.Count);
        return builder.ToString();
    }

    private static Visual BuildEmptyState()
        => new TextBlock(SR.T("Select a plugin on the left to inspect diagnostics, edit enablement, and open source or README files."))
        {
            Wrap = true,
        };

    private static (string Tone, string Icon) GetStatusToneAndIcon(PluginManagementState state)
        => state switch
        {
            PluginManagementState.Active => ("success", $"{TerminalIcons.MdCheckCircleOutline}"),
            PluginManagementState.Enabled => ("primary", $"{TerminalIcons.MdPuzzleCheckOutline}"),
            PluginManagementState.Disabled => ("muted", $"{TerminalIcons.MdPauseCircleOutline}"),
            PluginManagementState.Failed => ("error", $"{TerminalIcons.MdCloseCircleOutline}"),
            PluginManagementState.Changed => ("warning", $"{TerminalIcons.MdAlertOutline}"),
            PluginManagementState.UnknownConfig => ("warning", $"{TerminalIcons.MdPuzzleRemoveOutline}"),
            _ => ("primary", $"{TerminalIcons.MdPuzzleOutline}"),
        };

    private static string FormatStateText(PluginManagementState state)
        => state switch
        {
            PluginManagementState.Active => SR.T("active"),
            PluginManagementState.Enabled => SR.T("enabled"),
            PluginManagementState.Disabled => SR.T("disabled"),
            PluginManagementState.Failed => SR.T("failed"),
            PluginManagementState.Changed => SR.T("changed"),
            PluginManagementState.UnknownConfig => SR.T("unknown config"),
            _ => state.ToString(),
        };

    private static string FormatKind(PluginManagementEntry entry)
        => entry.LoadUnitKind == PluginLoadUnitKind.BuiltIn
            ? SR.T("built-in")
            : entry.Scope == PluginScope.Project
                ? SR.T("project source")
                : SR.T("global source");

    private static string GetDescription(PluginManagementEntry entry)
        => TryGetMetadata(entry, "Description") ?? string.Empty;

    private static string? TryGetMetadata(PluginManagementEntry entry, string key)
        => entry.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private void SetSelectedPluginIndex(int index)
    {
        var normalizedIndex = _plugins.Count == 0
            ? -1
            : Math.Clamp(index, 0, _plugins.Count - 1);
        _selectedPluginIndex.Value = normalizedIndex;
    }

    private int FindPluginIndex(string key)
    {
        for (var index = 0; index < _plugins.Count; index++)
        {
            if (string.Equals(_plugins[index].Entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private PluginManagementRow? GetSelectedRow()
        => _selectedPluginIndex.Value >= 0 && _selectedPluginIndex.Value < _plugins.Count
            ? _plugins[_selectedPluginIndex.Value]
            : null;

    private void Close()
    {
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private sealed class PluginManagementRow
    {
        public PluginManagementRow(PluginManagementEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Entry = entry;
            EnabledState = new State<bool>(entry.Enabled);
        }

        public PluginManagementEntry Entry { get; }

        public State<bool> EnabledState { get; }
    }
}
