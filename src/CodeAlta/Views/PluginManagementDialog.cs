using System.Text;
using CodeAlta.App;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
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
    private string _summaryText = "[dim]Plugin configuration has not been loaded yet.[/]";
    private string _statusText = "[dim]Use Refresh to reload plugin discovery and configuration.[/]";

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

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
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

        var refreshButton = new Button($"{NerdFont.MdRefresh} Refresh")
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

        var intro = new Markup("[dim]Source plugins are trusted code: build and load operations can execute local plugin or package build logic. Use --no-plugins, --plugin-safe-mode, or CODEALTA_DISABLE_PLUGINS=1 if a plugin breaks startup.[/]")
        {
            Wrap = true,
        };

        var leftPane = new VStack(
            new Group("Plugins")
                .Style(GroupStyle.Rounded)
                .Content(_pluginList.Stretch())
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            new Markup("[dim]Each row shows kind, scope, status, and a short description when available.[/]") { Wrap = true })
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        var rightPane = new Group("Plugin Details")
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
            .Title("Plugins")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 110, minHeight: 28, widthFactor: 0.84, heightFactor: 0.78);
        _dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Plugins.Manage.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the plugins dialog.",
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
                ? "[warning]No built-in or source plugins were discovered for the current scope.[/]"
                : "[dim]Select a plugin to inspect diagnostics, edit enablement, or open plugin files.[/]";

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
            _summaryText = "[error]Failed to load plugin management data.[/]";
            _statusText = $"[error]{AnsiMarkup.Escape(ex.GetBaseException().Message)}[/]";
        }
    }

    private Visual BuildDetailPane(PluginManagementRow row)
    {
        var entry = row.Entry;
        var enablement = new HStack(
            new CheckBox("Enabled").IsChecked(row.EnabledState),
            new Button("Apply")
                .Tone(ControlTone.Success)
                .IsEnabled(() => row.EnabledState.Value != entry.Enabled)
                .Click(() => ApplyEnablement(row)),
            new Markup(() => row.EnabledState.Value == entry.Enabled
                ? "[dim]Saved[/]"
                : "[warning]Unsaved enablement change[/]") { Wrap = false })
        {
            Spacing = 1,
        };

        var sourceButton = new Button($"{NerdFont.MdFileDocumentEditOutline} Open plugin.cs")
            .IsEnabled(!string.IsNullOrWhiteSpace(entry.SourcePath))
            .Click(() => _ = OpenFileAsync(entry.SourcePath, "plugin source"));
        var readmeButton = new Button($"{NerdFont.MdFileDocumentOutline} Open README")
            .IsEnabled(!string.IsNullOrWhiteSpace(entry.ReadmePath))
            .Click(() => _ = OpenFileAsync(entry.ReadmePath, "plugin README"));
        var rebuildButton = new Button($"{NerdFont.MdCogRefreshOutline} Rebuild")
            .IsEnabled(false);
        var reloadButton = new Button($"{NerdFont.MdReload} Reload")
            .IsEnabled(false);
        var cleanButton = new Button($"{NerdFont.MdDeleteSweepOutline} Clean")
            .IsEnabled(false);

        var actionPane = new VStack(
            new HStack(sourceButton, readmeButton, rebuildButton, reloadButton, cleanButton)
            {
                Spacing = 1,
            },
            new Markup("[dim]Open actions are available now. Rebuild, Reload, and Clean are shown in-place and will be enabled when runtime action handlers are connected.[/]") { Wrap = true })
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 1,
        };

        return new VStack(
            new Markup(BuildSelectedTitleMarkup(entry)) { Wrap = true },
            new Markup(BuildSelectedDescriptionMarkup(entry)) { Wrap = true },
            CreateSection("Enablement", enablement),
            CreateSection("Actions", actionPane),
            CreateSection("Properties", BuildPropertiesGrid(entry)),
            CreateSection("Diagnostics", new Markup(BuildDiagnosticsMarkup(entry)) { Wrap = true }),
            CreateSection("Contributions", new Markup(BuildContributionsMarkup(entry)) { Wrap = true }))
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
            _statusText = "[dim]No plugin enablement changes to save.[/]";
            return;
        }

        try
        {
            _service.SetPluginEnabled(row.Entry, enabled);
            var statusText = enabled
                ? "[success]Plugin enablement saved. Restart or reload plugins to apply runtime changes.[/]"
                : "[success]Plugin disablement saved. Restart or reload plugins to unload active runtime contributions.[/]";
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
            _statusText = $"[warning]This plugin does not have a {AnsiMarkup.Escape(label)} path.[/]";
            return;
        }

        try
        {
            await _openFileAsync(path, CancellationToken.None);
            _statusText = $"[success]Opened {AnsiMarkup.Escape(label)}.[/]";
        }
        catch (Exception ex)
        {
            _statusText = $"[error]Failed to open {AnsiMarkup.Escape(label)}:[/] {AnsiMarkup.Escape(ex.GetBaseException().Message)}";
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
            description = "No description was discovered for this plugin.";
        }

        return AnsiMarkup.Escape(description);
    }

    private static Visual BuildPropertiesGrid(PluginManagementEntry entry)
    {
        var rows = new List<(string Label, string? Value)>
        {
            ("Key", entry.Key),
            ("Plugin Id", entry.PluginId),
            ("Kind", entry.LoadUnitKind.ToString()),
            ("Scope", entry.Scope.ToString()),
            ("Configured", entry.Enabled ? "Enabled" : "Disabled"),
            ("Source", entry.SourcePath),
            ("README", entry.ReadmePath),
            ("Output", entry.OutputAssemblyPath),
            ("Build", entry.LastBuildSummary?.ToString()),
            ("Reason", TryGetMetadata(entry, "Reason")),
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
            return new TextBlock("No properties available.") { Wrap = true };
        }

        return grid;
    }

    private static string BuildDiagnosticsMarkup(PluginManagementEntry entry)
    {
        if (entry.Diagnostics.Count == 0)
        {
            return "[success]No diagnostics.[/]";
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
                ? "[dim]No active contributions were reported for this snapshot.[/]"
                : "[dim]Disabled plugins do not contribute runtime features.[/]";
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
        builder.Append("[bold]Status:[/] ");
        builder.Append(snapshot.SafeMode ? "[warning]safe mode enabled[/]" : "[success]plugins enabled by policy[/]");
        if (!string.IsNullOrWhiteSpace(snapshot.ProjectPath))
        {
            builder.Append("  [bold]Project root:[/] ").Append(AnsiMarkup.Escape(snapshot.ProjectPath));
        }

        builder.Append("  [bold]Plugins:[/] ").Append(snapshot.Entries.Count);
        return builder.ToString();
    }

    private static Visual BuildEmptyState()
        => new TextBlock("Select a plugin on the left to inspect diagnostics, edit enablement, and open source or README files.")
        {
            Wrap = true,
        };

    private static (string Tone, string Icon) GetStatusToneAndIcon(PluginManagementState state)
        => state switch
        {
            PluginManagementState.Active => ("success", $"{NerdFont.MdCheckCircleOutline}"),
            PluginManagementState.Enabled => ("primary", $"{NerdFont.MdPuzzleCheckOutline}"),
            PluginManagementState.Disabled => ("muted", $"{NerdFont.MdPauseCircleOutline}"),
            PluginManagementState.Failed => ("error", $"{NerdFont.MdCloseCircleOutline}"),
            PluginManagementState.Changed => ("warning", $"{NerdFont.MdAlertOutline}"),
            PluginManagementState.UnknownConfig => ("warning", $"{NerdFont.MdPuzzleRemoveOutline}"),
            _ => ("primary", $"{NerdFont.MdPuzzleOutline}"),
        };

    private static string FormatStateText(PluginManagementState state)
        => state switch
        {
            PluginManagementState.Active => "active",
            PluginManagementState.Enabled => "enabled",
            PluginManagementState.Disabled => "disabled",
            PluginManagementState.Failed => "failed",
            PluginManagementState.Changed => "changed",
            PluginManagementState.UnknownConfig => "unknown config",
            _ => state.ToString(),
        };

    private static string FormatKind(PluginManagementEntry entry)
        => entry.LoadUnitKind == PluginLoadUnitKind.BuiltIn
            ? "built-in"
            : entry.Scope == PluginScope.Project
                ? "project source"
                : "global source";

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
