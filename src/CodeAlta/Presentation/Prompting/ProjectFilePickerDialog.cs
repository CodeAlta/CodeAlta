using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Presentation.Prompting;

internal sealed class ProjectFilePickerDialog
{
    private const int DialogMinWidth = 64;
    private const int DialogMaxWidth = 140;
    private const int DialogMinHeight = 10;
    private const int DialogMaxHeight = 28;
    private const int ParentPathMaxLength = 88;
    private readonly TextBox _queryBox;
    private readonly OptionList<ProjectFileReferencePopupItem> _list;
    private readonly TextBlock _headerTextBlock;
    private readonly TextBlock _statisticsTextBlock;
    private readonly TextBlock _statusTextBlock;
    private readonly TextBlock _hintTextBlock;
    private readonly Dialog _dialog;
    private IReadOnlyList<ProjectFileReferencePopupItem> _items = [];
    private bool _isOpen;
    private bool _suppressListSelectionChanged;
    private bool _suppressQueryDocumentChanged;

    public ProjectFilePickerDialog(string hintText = "Arrows move · Enter insert link · Esc close")
    {
        _headerTextBlock = CreateLabel(string.Empty, static theme => theme.Foreground?.ToRgb() ?? Color.Default);
        _statisticsTextBlock = CreateLabel(string.Empty, UiPalette.GetPromptPlaceholderColor);
        _statusTextBlock = CreateLabel(string.Empty, UiPalette.GetPromptPlaceholderColor);
        _hintTextBlock = CreateLabel(hintText, UiPalette.GetPromptPlaceholderColor);

        _queryBox = new TextBox()
            .Placeholder("Search files and folders…")
            .HorizontalAlignment(Align.Stretch);
        _queryBox.TextDocument.Changed += OnQueryDocumentChanged;
        _queryBox.KeyDown((_, e) => HandleQueryKeyDown(e));

        _list = new OptionList<ProjectFileReferencePopupItem>()
            .ActivateOnClick(true)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .SelectionChanged((_, e) => OnListSelectionChanged(e.NewIndex))
            .ItemActivated((_, _) => AcceptRequested?.Invoke(this, EventArgs.Empty));
        _list.ItemTemplate = new DataTemplate<ProjectFileReferencePopupItem>(
            Display: static (DataTemplateValue<ProjectFileReferencePopupItem> value, in DataTemplateContext _) => BuildRow(value.GetValue()),
            Editor: null);
        _list.KeyDown((_, e) => HandleResultsKeyDown(e));

        var resultsHost = new ScrollViewer(_list, focusable: false)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .MinHeight(5)
            .MaxHeight(int.MaxValue);

        var content = new VStack(_queryBox, resultsHost)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 0,
        };

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

    public bool IsOpen => _isOpen;

    public string QueryText => _queryBox.Text ?? string.Empty;

    public IReadOnlyList<ProjectFileReferencePopupItem> Items => _items;

    public int SelectedIndex => _list.SelectedIndex;

    public void SetChrome(string headerText, string statisticsText, string statusText)
    {
        _headerTextBlock.Text = headerText ?? string.Empty;
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

    public void SetResults(IReadOnlyList<ProjectFileReferencePopupItem> items, int selectedIndex)
    {
        _items = items ?? [];

        _suppressListSelectionChanged = true;
        try
        {
            _list.Items.Clear();
            foreach (var item in _items)
            {
                _list.Items.Add(item);
            }

            _list.SelectedIndex = selectedIndex;
        }
        finally
        {
            _suppressListSelectionChanged = false;
        }
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

    private void OnListSelectionChanged(int newIndex)
    {
        if (_suppressListSelectionChanged)
        {
            return;
        }

        SelectionChanged?.Invoke(this, newIndex);
    }

    private void HandleQueryKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

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

    private bool TryMoveSelection(int delta)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        _list.SelectedIndex = Math.Clamp(Math.Max(_list.SelectedIndex, 0) + delta, 0, _items.Count - 1);
        return true;
    }

    private bool TryMoveSelectionToBoundary(bool first)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        _list.SelectedIndex = first ? 0 : _items.Count - 1;
        return true;
    }

    private bool FocusResults()
    {
        _dialog.App?.Focus(_list);
        return true;
    }

    private bool FocusQuery()
    {
        _dialog.App?.Focus(_queryBox);
        return true;
    }

    private bool RaiseAcceptRequested()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _items.Count)
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
        var width = ResolveDimension(availableWidth, DialogMinWidth, DialogMaxWidth, 0.56);
        var height = ResolveDimension(availableHeight, DialogMinHeight, DialogMaxHeight, 0.30);
        var left = Math.Max(0, (availableWidth - width) / 2);
        var top = Math.Max(0, (availableHeight - height) / 2 - 1);

        _dialog.MinWidth = DialogMinWidth;
        _dialog.MaxWidth = DialogMaxWidth;
        _dialog.MinHeight = DialogMinHeight;
        _dialog.MaxHeight = DialogMaxHeight;
        _dialog.Width = width;
        _dialog.Height = height;
        _dialog.Left = left;
        _dialog.Top = top;
    }

    private static int ResolveDimension(int available, int minimum, int maximum, double factor)
    {
        var scaled = (int)Math.Round(available * factor, MidpointRounding.AwayFromZero);
        return Math.Clamp(Math.Max(minimum, scaled), minimum, Math.Min(maximum, available));
    }

    private static TextBlock CreateLabel(string text, Func<Theme, Color> getForeground)
    {
        ArgumentNullException.ThrowIfNull(getForeground);

        TextBlock? label = null;
        label = new TextBlock(text)
        {
            Wrap = false,
            IsSelectable = false,
        }.Style(() => TextBlockStyle.Default with { Foreground = getForeground(label!.GetTheme()) });
        return label;
    }

    private static Visual BuildRow(ProjectFileReferencePopupItem item)
    {
        var label = new HStack(
        [
            new TextBlock(item.Appearance.Icon)
            {
                Wrap = false,
                IsSelectable = false,
            }.Style(TextBlockStyle.Default with { Foreground = item.Appearance.IconForeground }),
            new TextBlock(item.PrimaryText)
            {
                Wrap = false,
                IsSelectable = false,
            },
        ])
        {
            Spacing = 1,
        };

        Visual? shortcut = null;
        if (!string.IsNullOrWhiteSpace(item.SecondaryText))
        {
            shortcut = new TextBlock(ShortenParentPath(item.SecondaryText))
            {
                Wrap = false,
                IsSelectable = false,
            };
        }

        return new OptionListItem(label, shortcut);
    }

    private static string ShortenParentPath(string parentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);

        var normalized = parentPath.Replace('\\', '/');
        if (normalized.Length <= ParentPathMaxLength)
        {
            return normalized;
        }

        return "..." + normalized[^Math.Max(0, ParentPathMaxLength - 3)..];
    }
}
