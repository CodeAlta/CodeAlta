using XenoAtom.Terminal;
using XenoAtom.Terminal.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Plugin.GitHub;

internal sealed class GitHubIssuePickerDialog
{
    private const int DialogMinWidth = 76;
    private const int DialogMaxWidth = 150;
    private const int DialogMinHeight = 12;
    private const int DialogMaxHeight = 30;
    private readonly TextBox _queryBox;
    private readonly CheckBox _includeClosedCheckBox;
    private readonly DataGridListDocument<GitHubIssueReferenceItem> _document;
    private readonly DataGridControl _grid;
    private readonly TextBlock _headerTextBlock;
    private readonly TextBlock _statisticsTextBlock;
    private readonly TextBlock _statusTextBlock;
    private readonly TextBlock _hintTextBlock;
    private readonly Dialog _dialog;
    private IReadOnlyList<GitHubIssueReferenceItem> _items = [];
    private int _documentRowCount;
    private bool _isOpen;
    private bool _suppressQueryDocumentChanged;
    private bool _suppressSelectionChanged;

    public GitHubIssuePickerDialog(Action<string> openUrl, string hintText = "Arrows move · Enter insert link · Ctrl+I include closed · Esc close")
    {
        ArgumentNullException.ThrowIfNull(openUrl);
        _headerTextBlock = CreateLabel("GitHub issues");
        _statisticsTextBlock = CreateLabel(string.Empty);
        _statusTextBlock = CreateLabel(string.Empty);
        _hintTextBlock = CreateLabel(hintText);

        _queryBox = new TextBox()
            .Placeholder("Search GitHub issues…")
            .HorizontalAlignment(Align.Stretch);
        _queryBox.TextDocument.Changed += OnQueryDocumentChanged;
        _queryBox.KeyDown((_, e) => HandleQueryKeyDown(e));

        _includeClosedCheckBox = new CheckBox("Include closed (Ctrl+I)", isChecked: true)
        {
            Margin = new Thickness(2, 0, 0, 0),
        };
        _includeClosedCheckBox.ValueChanged((_, e) => IncludeClosedChanged?.Invoke(this, e.NewValue));
        _includeClosedCheckBox.KeyDown((_, e) => HandleFilterKeyDown(e));

        _document = new DataGridListDocument<GitHubIssueReferenceItem>();
        using (_document.BeginUpdate())
        {
            _document
                .AddColumn(new DataGridColumnInfo<string>("id", "🐙 Issue", true, GitHubIssueReferenceItem.Accessor.Id))
                .AddColumn(new DataGridColumnInfo<string>("title", "📝 Title", true, GitHubIssueReferenceItem.Accessor.Title))
                .AddColumn(new DataGridColumnInfo<string>("state", "🚦 State", true, GitHubIssueReferenceItem.Accessor.State))
                .AddColumn(new DataGridColumnInfo<string>("updated", "🕒 Updated", true, GitHubIssueReferenceItem.Accessor.Updated))
                .AddColumn(new DataGridColumnInfo<string>("link", "🔗 Link", true, GitHubIssueReferenceItem.Accessor.Link));
        }

        _grid = new DataGridControl { View = new DataGridDocumentView(_document) }
            .SelectionMode(DataGridSelectionMode.Row)
            .EditMode(DataGridEditMode.OnEnter)
            .ReadOnly(true)
            .ShowHeader(true)
            .ShowRowAnchor(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        ConfigureColumns(_grid, openUrl);
        _grid.KeyDown((_, e) => HandleResultsKeyDown(e));

        var content = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        content.Cell(_queryBox, 0, 0);
        content.Cell(
            new Border(new ScrollViewer(_grid.Stretch()).Stretch())
                .Style(BorderStyle.Rounded)
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            1,
            0);
        content.Cell(_includeClosedCheckBox, 2, 0);

        _dialog = new Dialog()
            .Title(_headerTextBlock)
            .TopRightText(_statisticsTextBlock)
            .BottomLeftText(_hintTextBlock)
            .BottomRightText(_statusTextBlock)
            .Padding(0)
            .IsModal(true)
            .IsDraggable(true)
            .IsResizable(true)
            .Content(content)
            .Style(DialogStyle.Rounded);
    }

    public event EventHandler<string>? QueryChanged;

    public event EventHandler<int>? SelectionChanged;

    public event EventHandler? AcceptRequested;

    public event EventHandler? DismissRequested;

    public event EventHandler<bool>? IncludeClosedChanged;

    public bool IsOpen => _isOpen;

    public string QueryText => _queryBox.Text ?? string.Empty;

    public int SelectedIndex => _grid.SelectedRow >= 0 ? _grid.SelectedRow : _grid.CurrentCell.Row;

    public bool IncludeClosed => _includeClosedCheckBox.IsChecked;

    public void SetChrome(string statisticsText, string statusText)
    {
        _statisticsTextBlock.Text = statisticsText ?? string.Empty;
        _statusTextBlock.Text = statusText ?? string.Empty;
    }

    public void SetQueryText(string queryText)
    {
        queryText ??= string.Empty;
        if (string.Equals(_queryBox.Text, queryText, StringComparison.Ordinal))
        {
            return;
        }

        _suppressQueryDocumentChanged = true;
        try
        {
            _queryBox.Text = queryText;
        }
        finally
        {
            _suppressQueryDocumentChanged = false;
        }
    }

    public void SetResults(IReadOnlyList<GitHubIssueReferenceItem> items, int selectedIndex)
    {
        _items = items ?? [];
        _suppressSelectionChanged = true;
        try
        {
            using (_document.BeginUpdate())
            {
                if (_documentRowCount > 0)
                {
                    _document.RemoveRows(0, _documentRowCount);
                }

                foreach (var item in _items)
                {
                    _document.AddRow(item);
                }
            }

            _documentRowCount = _items.Count;
            _grid.SelectedRow = selectedIndex;
            _grid.CurrentCell = selectedIndex >= 0 ? new DataGridCell(selectedIndex, 0) : DataGridCell.None;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        SelectionChanged?.Invoke(this, selectedIndex);
    }

    public void Show(TerminalApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (_isOpen)
        {
            app.Focus(_queryBox);
            return;
        }

        ApplyDialogGeometry(app.Root.Bounds);
        _dialog.Show();
        app.Focus(_queryBox);
        _isOpen = true;
    }

    public void Close()
    {
        if (!_isOpen)
        {
            return;
        }

        _dialog.Close();
        _isOpen = false;
    }

    private void OnQueryDocumentChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_suppressQueryDocumentChanged || !_isOpen)
        {
            return;
        }

        QueryChanged?.Invoke(this, QueryText);
    }

    private void HandleQueryKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (TryToggleIncludeClosed(e))
        {
            e.Handled = true;
            return;
        }

        var handled = e.Key switch
        {
            TerminalKey.Up => TryMoveSelection(-1),
            TerminalKey.Down => TryMoveSelection(1),
            TerminalKey.PageUp => TryMoveSelection(-8),
            TerminalKey.PageDown => TryMoveSelection(8),
            TerminalKey.Home => TryMoveSelectionToBoundary(first: true),
            TerminalKey.End => TryMoveSelectionToBoundary(first: false),
            TerminalKey.Enter => RaiseAcceptRequested(),
            TerminalKey.Escape => RaiseDismissRequested(),
            TerminalKey.Tab when _items.Count > 0 => FocusResults(),
            _ => false,
        };
        if (handled)
        {
            e.Handled = true;
        }
    }

    private void HandleResultsKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (TryToggleIncludeClosed(e))
        {
            e.Handled = true;
            return;
        }

        var handled = e.Key switch
        {
            TerminalKey.Enter => RaiseAcceptRequested(),
            TerminalKey.Tab => FocusQuery(),
            TerminalKey.Escape => RaiseDismissRequested(),
            _ => false,
        };
        if (handled)
        {
            e.Handled = true;
        }
    }

    private void HandleFilterKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (TryToggleIncludeClosed(e))
        {
            e.Handled = true;
        }
    }

    private bool TryToggleIncludeClosed(KeyEventArgs e)
    {
        if ((e.Modifiers & TerminalModifiers.Ctrl) == 0 || e.Char is not TerminalChar.CtrlI)
        {
            return false;
        }

        _includeClosedCheckBox.IsChecked = !_includeClosedCheckBox.IsChecked;
        return true;
    }

    private bool TryMoveSelection(int delta)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        var next = Math.Clamp(Math.Max(SelectedIndex, 0) + delta, 0, _items.Count - 1);
        _grid.SelectedRow = next;
        _grid.CurrentCell = new DataGridCell(next, 0);
        if (!_suppressSelectionChanged)
        {
            SelectionChanged?.Invoke(this, next);
        }

        return true;
    }

    private bool TryMoveSelectionToBoundary(bool first)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        var next = first ? 0 : _items.Count - 1;
        _grid.SelectedRow = next;
        _grid.CurrentCell = new DataGridCell(next, 0);
        if (!_suppressSelectionChanged)
        {
            SelectionChanged?.Invoke(this, next);
        }

        return true;
    }

    private bool FocusResults()
    {
        _dialog.App?.Focus(_grid);
        return true;
    }

    private bool FocusQuery()
    {
        _dialog.App?.Focus(_queryBox);
        return true;
    }

    private bool RaiseAcceptRequested()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _items.Count)
        {
            return false;
        }

        AcceptRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool RaiseDismissRequested()
    {
        DismissRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ApplyDialogGeometry(in Rectangle viewport)
    {
        var availableWidth = Math.Max(DialogMinWidth, viewport.Width);
        var availableHeight = Math.Max(DialogMinHeight, viewport.Height);
        var width = ResolveDimension(availableWidth, DialogMinWidth, DialogMaxWidth, 0.68);
        var height = ResolveDimension(availableHeight, DialogMinHeight, DialogMaxHeight, 0.34);
        _dialog.MinWidth = DialogMinWidth;
        _dialog.MaxWidth = DialogMaxWidth;
        _dialog.MinHeight = DialogMinHeight;
        _dialog.MaxHeight = DialogMaxHeight;
        _dialog.Width = width;
        _dialog.Height = height;
        _grid.MinWidth = Math.Max(0, width - 4);
        _dialog.Left = Math.Max(0, (availableWidth - width) / 2);
        _dialog.Top = Math.Max(0, (availableHeight - height) / 2 - 1);
    }

    private static int ResolveDimension(int available, int minimum, int maximum, double factor)
    {
        var scaled = (int)Math.Round(available * factor, MidpointRounding.AwayFromZero);
        return Math.Clamp(Math.Max(minimum, scaled), minimum, Math.Min(maximum, available));
    }

    private static void ConfigureColumns(DataGridControl grid, Action<string> openUrl)
    {
        static Visual BuildIdCell(DataTemplateValue<string> value, in DataTemplateContext _)
        {
            return new TextBlock(value.GetValue()) { Wrap = false, IsSelectable = false };
        }

        static Visual BuildTitleCell(DataTemplateValue<string> value, in DataTemplateContext _)
        {
            var row = (GitHubIssueReferenceItem)value.GetBinding().Owner;
            return new TextBlock(value.GetValue()) { Wrap = false, IsSelectable = false }
                .Tooltip(new TextBlock(string.IsNullOrWhiteSpace(row.Url) ? row.Title : $"{row.Title}\n{row.Url}").Wrap(true));
        }

        static Visual BuildStateCell(DataTemplateValue<string> value, in DataTemplateContext _)
        {
            TextBlock? state = null;
            state = new TextBlock(value.GetValue()) { Wrap = false, IsSelectable = false }
                .Style(() => TextBlockStyle.Default with { Foreground = GetIssueStateColor(state!.GetTheme(), value.GetValue()) });
            return state;
        }

        static Visual BuildUpdatedCell(DataTemplateValue<string> value, in DataTemplateContext _)
        {
            return new TextBlock(value.GetValue()) { Wrap = false, IsSelectable = false };
        }

        Visual BuildLinkCell(DataTemplateValue<string> value, in DataTemplateContext _)
        {
            var row = (GitHubIssueReferenceItem)value.GetBinding().Owner;
            if (string.IsNullOrWhiteSpace(row.Url))
            {
                return new TextBlock(string.Empty) { Wrap = false, IsSelectable = false };
            }

            return new Button("↗")
                .Click(() => openUrl(row.Url))
                .Tooltip(new TextBlock(row.Url).Wrap(true));
        }

        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "id",
            Header = new TextBlock("🐙 Issue"),
            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Id,
            Width = GridLength.Auto,
            Sortable = true,
            CellTemplate = new DataTemplate<string>(BuildIdCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "title",
            Header = new TextBlock("📝 Title"),
            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Title,
            Width = GridLength.Star(1),
            Sortable = true,
            CellTemplate = new DataTemplate<string>(BuildTitleCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "state",
            Header = new TextBlock("🚦 State"),
            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.State,
            Width = GridLength.Auto,
            Sortable = true,
            CellTemplate = new DataTemplate<string>(BuildStateCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "updated",
            Header = new TextBlock("🕒 Updated"),
            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Updated,
            Width = GridLength.Auto,
            Sortable = true,
            CellTemplate = new DataTemplate<string>(BuildUpdatedCell, null),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "link",
            Header = new TextBlock("🔗 Link"),
            TypedValueAccessor = GitHubIssueReferenceItem.Accessor.Link,
            Width = GridLength.Auto,
            Sortable = false,
            CellTemplate = new DataTemplate<string>(BuildLinkCell, null),
        });
    }

    private static Color GetIssueStateColor(Theme theme, string state)
        => state switch
        {
            _ when string.Equals(state, "open", StringComparison.OrdinalIgnoreCase) => theme.Scheme?.BrightGreen ?? theme.Success ?? theme.Primary ?? theme.Foreground ?? Color.Default,
            _ when string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase) => theme.Scheme?.BrightRed ?? theme.Error ?? theme.Warning ?? theme.Foreground ?? Color.Default,
            _ => theme.Scheme?.BrightYellow ?? theme.Warning ?? theme.Foreground ?? Color.Default,
        };

    private static TextBlock CreateLabel(string text)
        => new(text)
        {
            Wrap = false,
            IsSelectable = false,
        };
}
