using CodeAlta.Presentation.Sidebar;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class SidebarNodeHeaderView : Visual
{
    private readonly SidebarNodeViewModel _row;
    private readonly Action<SidebarNodeViewModel> _submitInlineRename;
    private readonly Action<SidebarNodeViewModel> _cancelInlineRename;
    private readonly TextBlock _title;
    private readonly TextBox _editor;
    private readonly TextBlock _validationIcon;
    private readonly ComputedVisual _validationIndicator;
    private readonly HStack _inlineEditor;
    private readonly ComputedVisual _content;
    private readonly Spinner _stateSpinner;
    private readonly ComputedVisual _stateIndicator;
    private readonly HStack _layout;
    private bool _focusEditorPending;

    public SidebarNodeHeaderView(
        SidebarNodeViewModel row,
        Action<SidebarNodeViewModel> submitInlineRename,
        Action<SidebarNodeViewModel> cancelInlineRename)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(submitInlineRename);
        ArgumentNullException.ThrowIfNull(cancelInlineRename);

        _row = row;
        _submitInlineRename = submitInlineRename;
        _cancelInlineRename = cancelInlineRename;

        HorizontalAlignment = Align.Stretch;

        _title = new TextBlock(() => _row.Title)
        {
            Wrap = false,
            HorizontalAlignment = Align.Stretch,
        }
        .Style(TextBlockStyle.Default with { TextStyle = TextStyle.Bold });
        _title.Trimming(TextTrimming.EndEllipsis);

        _editor = new TextBox
        {
            Text = _row.InlineEditText,
            HorizontalAlignment = Align.Stretch,
        };
        _editor.KeyDown((_, e) => OnEditorKeyDown(e));

        _validationIcon = new TextBlock(NerdFont.MdAlertCircleOutline.ToString());
        _validationIcon.Style(() => TextBlockStyle.Default with { Foreground = _validationIcon.GetTheme().Error ?? _validationIcon.GetTheme().Foreground ?? Color.Default });
        _validationIndicator = new ComputedVisual(() => _row.InlineEditValidationMessage is null ? null : _validationIcon)
        {
            Margin = new Thickness(1, 0, 0, 0),
        };

        _inlineEditor = new HStack(_editor, _validationIndicator)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Center,
        };

        _content = new ComputedVisual(() => _row.IsInlineEditing ? _inlineEditor : _title)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Center,
        };

        _stateSpinner = new Spinner().Style(SpinnerStyles.Dots);
        _stateSpinner.IsActive(() => _row.ShowStateSpinner);
        _stateSpinner.IsVisible(() => _row.ShowStateSpinner);
        _stateIndicator = new ComputedVisual(
            () =>
            {
                if (_row.ShowStateSpinner)
                {
                    return _stateSpinner;
                }

                if (string.IsNullOrWhiteSpace(_row.StateIconMarkup))
                {
                    return null;
                }

                var stateIcon = new Markup(() => _row.StateIconMarkup!)
                {
                    Wrap = false,
                };
                return string.IsNullOrWhiteSpace(_row.StateTooltip)
                    ? stateIcon
                    : stateIcon.Tooltip(new TextBlock(() => _row.StateTooltip!));
            })
        {
            VerticalAlignment = Align.Center,
        };
        _layout = new HStack(_stateIndicator, _content)
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Center,
        };
        AttachChild(_layout);
    }

    protected override int ChildrenCount => 1;

    protected override Visual GetChild(int index)
        => index == 0 ? _layout : throw new ArgumentOutOfRangeException(nameof(index));

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
        => _layout.Measure(constraints);

    protected override void ArrangeCore(in Rectangle finalRect)
        => _layout.Arrange(finalRect);

    private void OnEditorKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case TerminalKey.Enter:
                _row.InlineEditText = _editor.Text ?? string.Empty;
                _submitInlineRename(_row);
                if (_row.IsInlineEditing)
                {
                    RequestEditorFocus();
                }
                e.Handled = true;
                break;
            case TerminalKey.Escape:
                _cancelInlineRename(_row);
                e.Handled = true;
                break;
            case TerminalKey.Up:
            case TerminalKey.Down:
                e.Handled = true;
                break;
        }
    }

    public void RequestEditorFocus()
    {
        if (!_row.IsInlineEditing)
        {
            return;
        }

        _focusEditorPending = true;
        Dispatcher.Post(TryFocusEditor);
    }

    private void TryFocusEditor()
    {
        if (!_focusEditorPending)
        {
            return;
        }

        if (!_row.IsInlineEditing)
        {
            _focusEditorPending = false;
            return;
        }

        if (_editor.App is not { } app)
        {
            Dispatcher.Post(TryFocusEditor);
            return;
        }

        _editor.Text = _row.InlineEditText;
        app.Focus(_editor);
        var selectAllCommand = _editor.Commands.FirstOrDefault(static command => string.Equals(command.Id, "TextEditor.SelectAll", StringComparison.Ordinal));
        selectAllCommand?.Execute(_editor);
        _focusEditorPending = false;
    }
}
