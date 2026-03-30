using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Threading;
using CodeAlta.ViewModels;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class ProjectThreadsDialog
{
    private readonly ProjectThreadsDialogState _state;
    private readonly ProjectThreadsDialogViewModel _viewModel = new();
    private readonly Func<IUiDispatcher> _getUiDispatcher;
    private readonly Func<IReadOnlyList<string>, Task> _deleteThreadsAsync;
    private readonly Func<string, Task> _openThreadAsync;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly DataGridListDocument<ProjectThreadsDialogRowViewModel> _document;
    private readonly Dialog _dialog;
    private int _documentRowCount;

    public ProjectThreadsDialog(
        ProjectDescriptor project,
        IReadOnlyList<WorkThreadDescriptor> threads,
        Func<IReadOnlyList<string>, Task> deleteThreadsAsync,
        Func<string, Task> openThreadAsync,
        Func<IUiDispatcher> getUiDispatcher,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(deleteThreadsAsync);
        ArgumentNullException.ThrowIfNull(openThreadAsync);
        ArgumentNullException.ThrowIfNull(getUiDispatcher);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _deleteThreadsAsync = deleteThreadsAsync;
        _openThreadAsync = openThreadAsync;
        _getUiDispatcher = getUiDispatcher;
        _getFocusTarget = getFocusTarget;

        var nowUtc = DateTimeOffset.UtcNow;
        _state = new ProjectThreadsDialogState(
            threads
                .Where(static thread => thread.Status != WorkThreadStatus.Archived)
                .OrderByDescending(static thread => thread.LastActiveAt)
                .ThenBy(static thread => thread.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static thread => thread.ThreadId, StringComparer.OrdinalIgnoreCase)
                .Select(thread => CreateRow(thread, nowUtc))
                .ToArray());

        var rowAccessor = new BindingAccessor<ProjectThreadsDialogRowViewModel>(
            "row",
            static value => (ProjectThreadsDialogRowViewModel)value,
            static (_, _) => { });

        _document = new DataGridListDocument<ProjectThreadsDialogRowViewModel>();
        using (_document.BeginUpdate())
        {
            _document
                .AddColumn(new DataGridColumnInfo<bool>("select", string.Empty, false, ProjectThreadsDialogRowViewModel.Accessor.IsSelected))
                .AddColumn(ProjectThreadsDialogRowViewModel.Accessor.Title)
                .AddColumn(new DataGridColumnInfo<DateTimeOffset?>("updated", "Last Updated", true, ProjectThreadsDialogRowViewModel.Accessor.LastUpdatedAt))
                .AddColumn(ProjectThreadsDialogRowViewModel.Accessor.MessageCount)
                .AddColumn(new DataGridColumnInfo<ProjectThreadsDialogRowViewModel>("open", "Action", false, rowAccessor));

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

        Visual BuildSelectionCell(DataTemplateValue<ProjectThreadsDialogRowViewModel> value, in DataTemplateContext _)
        {
            var row = value.GetValue();
            return new CheckBox(new TextBlock(""), row.Bind.IsSelected);
        }

        static Visual BuildLastUpdatedCell(DataTemplateValue<DateTimeOffset?> value, in DataTemplateContext _)
        {
            var row = value.GetValue();
            return new Markup(() => row.ToString())
                .Wrap(false);
/*                .Tooltip(new TextBlock(() => row.LastUpdatedExact))*/;
        }

        static Visual BuildMessageCountCell(DataTemplateValue<int?> value, in DataTemplateContext _)
            => new TextBlock(value.GetValue()?.ToString() ?? "—");

        Visual BuildOpenCell(DataTemplateValue<ProjectThreadsDialogRowViewModel> value, in DataTemplateContext context)
        {
            var row = value.GetValue();
            return new Button("Open")
                .Tone(ControlTone.Primary)
                .Click(() => _ = OpenThreadAsync(row.ThreadId));
        }

        grid.Columns.Add(new DataGridColumn<bool>
        {
            Key = "select",
            Header = new TextBlock(""),
            TypedValueAccessor = ProjectThreadsDialogRowViewModel.Accessor.IsSelected,
            Width = GridLength.Auto,
            //CellTemplate = new DataTemplate<ProjectThreadsDialogRowViewModel>(BuildSelectionCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = ProjectThreadsDialogRowViewModel.Accessor.Title.Name,
            Header = new TextBlock("Thread Title"),
            TypedValueAccessor = ProjectThreadsDialogRowViewModel.Accessor.Title,
            Width = GridLength.Star(2),
            Sortable = true,
        });
        grid.Columns.Add(new DataGridColumn<DateTimeOffset?>
        {
            Key = "updated",
            Header = new TextBlock("Last Updated"),
            TypedValueAccessor = ProjectThreadsDialogRowViewModel.Accessor.LastUpdatedAt,
            Width = GridLength.Auto,
            Sortable = true,
            CellTemplate = new DataTemplate<DateTimeOffset?>(BuildLastUpdatedCell, null),
        });
        grid.Columns.Add(new DataGridColumn<int?>
        {
            Key = ProjectThreadsDialogRowViewModel.Accessor.MessageCount.Name,
            Header = new TextBlock("Messages"),
            TypedValueAccessor = ProjectThreadsDialogRowViewModel.Accessor.MessageCount,
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
        grid.Columns.Add(new DataGridColumn<ProjectThreadsDialogRowViewModel>
        {
            Key = "open",
            Header = new TextBlock("Action"),
            TypedValueAccessor = rowAccessor,
            Width = GridLength.Auto,
            ReadOnly = true,
            CellTemplate = new DataTemplate<ProjectThreadsDialogRowViewModel>(BuildOpenCell, null),
        });

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var toolbar = new HStack(
            new Button("Select None").Click(_state.SelectNone),
            new Button("Select All").Click(_state.SelectAll),
            new Button("Invert").Click(_state.InvertSelection),
            new Button("Delete Selected")
                .Tone(ControlTone.Error)
                .Click(() => _ = ConfirmDeleteSelectedAsync()),
            new Button(() => _viewModel.FilterRowVisible ? "Hide Filter" : "Show Filter")
                .Click(() => _viewModel.FilterRowVisible = !_viewModel.FilterRowVisible),
            new TextBlock(() => $"{_state.SelectedCount} selected"))
        {
            HorizontalAlignment = Align.Stretch,
            Spacing = 2,
        };

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Star(1) });

        content.Cells.Add(new GridCell() { Column = 0, Row = 0, Content = toolbar });
        content.Cells.Add(new GridCell() { Column = 0, Row = 1, Content = new ScrollViewer(grid.Stretch()).Stretch() });

        _dialog = new Dialog()
            .Title($"Threads · {project.DisplayName}")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 80, minHeight: 16);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ProjectThreads.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close the project thread list dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
        => _dialog.Show();

    private async Task ConfirmDeleteSelectedAsync()
    {
        var selectedThreadIds = _state.GetSelectedThreadIds();
        if (selectedThreadIds.Count == 0)
        {
            return;
        }

        new ConfirmationDialog(
            "Delete Selected Threads",
            [$"Delete {selectedThreadIds.Count} selected thread(s)?"],
            "Delete",
            ControlTone.Error,
            async () =>
            {
                await _deleteThreadsAsync(selectedThreadIds).ConfigureAwait(false);
                await _getUiDispatcher().InvokeAsync(() =>
                {
                    _state.RemoveThreads(selectedThreadIds);
                    RebuildDocumentRows();
                }).ConfigureAwait(false);
            },
            () => _dialog.GetAbsoluteBounds(),
            () => _dialog)
            .Show();
    }

    private async Task OpenThreadAsync(string threadId)
    {
        Close();
        await _openThreadAsync(threadId).ConfigureAwait(false);
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

    private static ProjectThreadsDialogRowViewModel CreateRow(WorkThreadDescriptor thread, DateTimeOffset nowUtc)
    {
        var display = SidebarRelativeTimeFormatter.Format(thread.LastActiveAt, nowUtc);
        return new ProjectThreadsDialogRowViewModel
        {
            ThreadId = thread.ThreadId,
            Title = thread.Title,
            LastUpdatedAt = thread.LastActiveAt,
            LastUpdatedRelative = display.RelativeText,
            LastUpdatedExact = display.ExactText,
            MessageCount = thread.MessageCount,
            ProjectId = thread.ProjectRef,
        };
    }
}
