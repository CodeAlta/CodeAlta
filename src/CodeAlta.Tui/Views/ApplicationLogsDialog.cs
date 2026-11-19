using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Catalog;
using CodeAlta.Tui.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tui.Views;

internal sealed class ApplicationLogsDialog
{
    private readonly CodeAltaUiLogBuffer _buffer;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly State<bool> _wrapText = new(true);
    private readonly LogControl _logControl;
    private readonly Dialog _dialog;
    private IDisposable? _subscription;
    // The dispatcher of the application the dialog is shown in: the log control has one of its own only once it is attached.
    private XenoAtom.Terminal.UI.Threading.Dispatcher? _dispatcher;

    public ApplicationLogsDialog(
        CodeAltaUiLogBuffer buffer,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _buffer = buffer;
        _getFocusTarget = getFocusTarget;

        _logControl = new LogControl
        {
            MaxCapacity = CodeAltaLogging.UiLogCapacity,
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }.WrapText(_wrapText);

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var clearButton = new Button($"{TerminalIcons.MdDeleteOutline} {SR.T("Clear Logs")}")
        {
            Tone = ControlTone.Default,
        };
        clearButton.Click(_buffer.Clear);

        var toolbar = new HStack(
        [
            clearButton,
            new CheckBox(SR.T("Wrap")).IsChecked(_wrapText),
        ])
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Start,
            Spacing = 2,
        };

        var logHost = new ScrollViewer(_logControl.Stretch(), focusable: false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        var content = new Grid()
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) })
            .Cell(toolbar, 0, 0)
            .Cell(logHost.Stretch(), 1, 0);

        _dialog = new Dialog()
            .Title(SR.T("Application Logs"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Ctrl+F Search")} · {SR.T("Esc")} {SR.T("Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 72, minHeight: 18, widthFactor: 0.8, heightFactor: 0.8);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ApplicationLogs.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the application logs dialog."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
        _dialog.KeyDown((_, e) =>
        {
            if (e.Key != TerminalKey.Escape)
            {
                return;
            }

            Close();
            e.Handled = true;
        });
    }

    public void Show()
    {
        // Shown from the UI thread: the loggers of the other threads reach the control through its dispatcher from
        // their first entry on, which can come before the dialog is attached.
        _dispatcher = XenoAtom.Terminal.UI.Threading.Dispatcher.Current;
        _logControl.Clear();
        _subscription = _buffer.Subscribe(HandleBufferEvent, out var snapshot);
        foreach (var entry in snapshot)
        {
            AppendEntry(entry);
        }

        if (snapshot.Length > 0)
        {
            _logControl.ScrollToTail();
        }

        _dialog.Show();
    }

    private void HandleBufferEvent(CodeAltaUiLogBufferEvent @event)
    {
        switch (@event.Kind)
        {
            case CodeAltaUiLogBufferEventKind.Appended when @event.Entry is { } entry:
                AppendEntry(entry);
                break;
            case CodeAltaUiLogBufferEventKind.Cleared:
                ClearEntries();
                break;
        }
    }

    private void AppendEntry(CodeAltaUiLogEntry entry)
        => RunOnUi(() => AppendEntryCore(entry));

    private void ClearEntries()
        => RunOnUi(_logControl.Clear);

    private void RunOnUi(Action action)
    {
        if (_dispatcher is not { } dispatcher || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.Post(action);
        }
        catch (InvalidOperationException)
        {
            // The application has stopped: its dialog shows nothing any more.
        }
    }

    private void AppendEntryCore(CodeAltaUiLogEntry entry)
    {
        if (entry.IsMarkup)
        {
            _logControl.AppendMarkupLine(entry.Text);
        }
        else
        {
            _logControl.AppendLine(entry.Text);
        }
    }

    private void Close()
    {
        DisposeSubscription();
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }

    private void DisposeSubscription()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
