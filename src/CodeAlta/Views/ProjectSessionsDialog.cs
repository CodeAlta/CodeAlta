using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class ProjectSessionsDialog
{
    private readonly ProjectSessionsDialogState _state;
    private readonly ProjectSessionsDialogViewModel _viewModel = new();
    private readonly Func<IReadOnlyList<string>, Task> _deleteSessionsAsync;
    private readonly Func<string, Task> _openSessionAsync;
    private readonly Func<string?, string> _resolveProviderDisplayName;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly DataGridListDocument<ProjectSessionsDialogRowViewModel> _document;
    private readonly Dialog _dialog;
    private int _documentRowCount;

    public ProjectSessionsDialog(
        ProjectDescriptor project,
        IReadOnlyList<SessionViewDescriptor> sessions,
        Func<string?, string> resolveProviderDisplayName,
        Func<IReadOnlyList<string>, Task> deleteSessionsAsync,
        Func<string, Task> openSessionAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(resolveProviderDisplayName);
        ArgumentNullException.ThrowIfNull(deleteSessionsAsync);
        ArgumentNullException.ThrowIfNull(openSessionAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _resolveProviderDisplayName = resolveProviderDisplayName;
        _deleteSessionsAsync = deleteSessionsAsync;
        _openSessionAsync = openSessionAsync;
        _getFocusTarget = getFocusTarget;

        var nowUtc = DateTimeOffset.UtcNow;
        _state = new ProjectSessionsDialogState(
            sessions
                .Where(static session => session.Status != SessionViewStatus.Archived)
                .OrderByDescending(static session => session.LastActiveAt)
                .ThenBy(static session => session.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static session => session.SessionId, StringComparer.OrdinalIgnoreCase)
                .Select(session => CreateRow(session, nowUtc, _resolveProviderDisplayName))
                .ToArray());

        _document = new DataGridListDocument<ProjectSessionsDialogRowViewModel>();
        using (_document.BeginUpdate())
        {
            _document
                .AddColumn(new DataGridColumnInfo<bool>("select", "✅", false, ProjectSessionsDialogRowViewModel.Accessor.IsSelected))
                .AddColumn(new DataGridColumnInfo<string>("provider", $"🤖 {SR.T("Provider")}", true, ProjectSessionsDialogRowViewModel.Accessor.ProviderDisplayName))
                .AddColumn(new DataGridColumnInfo<string>("title", $"🧵 {SR.T("Session")}", false, ProjectSessionsDialogRowViewModel.Accessor.Title))
                .AddColumn(new DataGridColumnInfo<DateTimeOffset?>("updated", $"🕒 {SR.T("Updated")}", true, ProjectSessionsDialogRowViewModel.Accessor.LastUpdatedAt))
                .AddColumn(new DataGridColumnInfo<int?>("messages", $"💬 {SR.T("Messages")}", true, ProjectSessionsDialogRowViewModel.Accessor.MessageCount))
                .AddColumn(new DataGridColumnInfo<string>("open", $"🚀 {SR.T("Open")}", false, ProjectSessionsDialogRowViewModel.Accessor.SessionId));

            foreach (var row in _state.Rows)
            {
                _document.AddRow(row);
            }
        }

        _documentRowCount = _state.Rows.Count;
        var view = new DataGridDocumentView(_document);
        var grid = new DataGridControl { View = view }
            .FilterRowVisible(_viewModel.Bind.FilterRowVisible)
            .SelectionMode(DataGridSelectionMode.Cell)
            .EditMode(DataGridEditMode.OnEnter)
            .ShowHeader(true)
            .ShowRowAnchor(false);

        static Visual BuildLastUpdatedCell(DataTemplateValue<DateTimeOffset?> value, in DataTemplateContext _)
        {
            var row = (ProjectSessionsDialogRowViewModel)value.GetBinding().Owner;
            return new TextBlock(() => row.LastUpdatedRelative)
                .Tooltip(new TextBlock(() => row.LastUpdatedExact));
        }

        static Visual BuildMessageCountCell(DataTemplateValue<int?> value, in DataTemplateContext _)
            => new TextBlock(value.GetValue()?.ToString() ?? "—");

        static Visual BuildProviderCell(DataTemplateValue<string> value, in DataTemplateContext _)
        {
            var row = (ProjectSessionsDialogRowViewModel)value.GetBinding().Owner;
            return new Markup(() => SidebarSessionPresentation.BuildProviderMarkup(row.ProviderId, row.ProviderDisplayName, row.SessionKind))
                .Wrap(false)
                .Tooltip(new TextBlock(() => string.IsNullOrWhiteSpace(row.ProviderId)
                    ? row.ProviderDisplayName
                    : $"{row.ProviderDisplayName} ({row.ProviderId})"));
        }

        static Visual BuildOpenButtonDisplay(DataTemplateValue<string> value, in DataTemplateContext context)
        {
            _ = value;
            _ = context;
            return new Button(SR.T("Open"))
            {
                IsHitTestVisible = false,
            }
                .Tone(ControlTone.Primary);
        }

        Visual BuildOpenButtonEditor(Binding<string> binding, in DataTemplateContext context)
        {
            _ = context;
            return new Button(SR.T("Open"))
                .Tone(ControlTone.Primary)
                .Click(() => _ = OpenSessionAsync(binding.GetValue()));
        }

        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "select",
            Header = new TextBlock("✅"),
            TypedValueAccessor = ProjectSessionsDialogRowViewModel.Accessor.IsSelected,
            Width = GridLength.Auto,
            //CellTemplate = new DataTemplate<ProjectSessionsDialogRowViewModel>(BuildSelectionCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "title",
            Header = new TextBlock($"🧵 {SR.T("Session")}"),
            TypedValueAccessor = ProjectSessionsDialogRowViewModel.Accessor.Title,
            Width = GridLength.Star(2),
            Sortable = true,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "provider",
            Header = new TextBlock($"🤖 {SR.T("Provider")}"),
            TypedValueAccessor = ProjectSessionsDialogRowViewModel.Accessor.ProviderDisplayName,
            Width = GridLength.Auto,
            Sortable = true,
            CellTemplate = new DataTemplate<string>(BuildProviderCell, null),
        });
        grid.Columns.Add(new DataGridColumn<DateTimeOffset?>
        {
            Key = "updated",
            Header = new TextBlock($"🕒 {SR.T("Updated")}"),
            TypedValueAccessor = ProjectSessionsDialogRowViewModel.Accessor.LastUpdatedAt,
            Width = GridLength.Auto,
            Sortable = true,
            SortComparer = Comparer<DateTimeOffset?>.Create(static (left, right) => Nullable.Compare(left, right)),
            CellTemplate = new DataTemplate<DateTimeOffset?>(BuildLastUpdatedCell, null),
        });
        grid.Columns.Add(new DataGridColumn<int?>
        {
            Key = "messages",
            Header = new TextBlock($"💬 {SR.T("Messages")}"),
            TypedValueAccessor = ProjectSessionsDialogRowViewModel.Accessor.MessageCount,
            Width = GridLength.Auto,
            CellAlignment = TextAlignment.Right,
            Sortable = true,
            SortComparer = Comparer<int?>.Create(static (left, right) =>
            {
                if (left == right)
                {
                    return 0;
                }

                if (left is null)
                {
                    return 1;
                }

                if (right is null)
                {
                    return -1;
                }

                return left.Value.CompareTo(right.Value);
            }),
            CellTemplate = new DataTemplate<int?>(BuildMessageCountCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "open",
            Header = new TextBlock($"🚀 {SR.T("Open")}"),
            TypedValueAccessor = ProjectSessionsDialogRowViewModel.Accessor.SessionId,
            Width = GridLength.Auto,
            CellActivationMode = DataGridCellActivationMode.DirectActivate,
            CellTemplate = new DataTemplate<string>(BuildOpenButtonDisplay, null),
            CellEditorTemplate = new DataTemplate<string>(null, BuildOpenButtonEditor),
        });

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var toolbar = new HStack(
            new Button(SR.T("Select None")).Click(_state.SelectNone),
            new Button(SR.T("Select All")).Click(_state.SelectAll),
            new Button(SR.T("Invert")).Click(_state.InvertSelection),
            new Button(SR.T("Delete Selected"))
                .Tone(ControlTone.Error)
                .Click(() => _ = ConfirmDeleteSelectedAsync()),
            new CheckBox(SR.T("Filter row")).IsChecked(_viewModel.Bind.FilterRowVisible),
            new TextBlock(() => SR.T("{0} selected", _state.SelectedCount)))
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 2,
        }.Pad(new(0, 0, 0, 1));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Star(1) });

        content.Cells.Add(new GridCell() { Column = 0, Row = 0, Content = toolbar });
        content.Cells.Add(new GridCell() { Column = 0, Row = 1, Content = new ScrollViewer(grid.Stretch()).Stretch() });

        _dialog = new Dialog()
            .Title($"{SR.T("Sessions")} · {project.DisplayName}")
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 80, minHeight: 16);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ProjectSessions.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the project session list dialog."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
        => _dialog.Show();

    private async Task ConfirmDeleteSelectedAsync()
    {
        var selectedSessionIds = _state.GetSelectedSessionIds();
        if (selectedSessionIds.Count == 0)
        {
            return;
        }

        new ConfirmationDialog(
            SR.T("Delete Selected Sessions"),
            [SR.T("Delete {0} selected session(s)?", selectedSessionIds.Count)],
            SR.T("Delete"),
            ControlTone.Error,
            async () =>
            {
                await _deleteSessionsAsync(selectedSessionIds);
                _state.RemoveSessions(selectedSessionIds);
                RebuildDocumentRows();
            },
            () => _dialog.GetAbsoluteBounds(),
            () => _dialog)
            .Show();
    }

    private async Task OpenSessionAsync(string sessionId)
    {
        Close();
        await _openSessionAsync(sessionId);
    }

    private void RebuildDocumentRows()
    {
        using (_document.BeginUpdate())
        {
            if (_documentRowCount > 0)
            {
                _document.RemoveRows(0, _documentRowCount);
            }

            foreach (var row in _state.Rows)
            {
                _document.AddRow(row);
            }
        }

        _documentRowCount = _state.Rows.Count;
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

    private static ProjectSessionsDialogRowViewModel CreateRow(
        SessionViewDescriptor session,
        DateTimeOffset nowUtc,
        Func<string?, string> resolveProviderDisplayName)
    {
        ArgumentNullException.ThrowIfNull(resolveProviderDisplayName);

        var display = SidebarRelativeTimeFormatter.Format(session.LastActiveAt, nowUtc);
        return new ProjectSessionsDialogRowViewModel
        {
            SessionId = session.SessionId,
            Title = session.Title,
            ProviderId = session.ProviderId,
            ProviderDisplayName = resolveProviderDisplayName(session.ProviderId),
            SessionKind = session.Kind,
            LastUpdatedAt = session.LastActiveAt,
            LastUpdatedRelative = display.RelativeText,
            LastUpdatedExact = display.ExactText,
            MessageCount = session.MessageCount,
            ProjectId = session.ProjectRef,
        };
    }
}
