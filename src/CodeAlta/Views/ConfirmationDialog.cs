using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class ConfirmationDialog
{
    private readonly Dialog _dialog;
    private readonly Func<Task> _onConfirmAsync;
    private readonly Func<Visual?> _getFocusTarget;

    public ConfirmationDialog(
        string title,
        IReadOnlyList<string> bodyLines,
        string confirmText,
        ControlTone confirmTone,
        Func<Task> onConfirmAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget,
        string cancelText = "Cancel",
        string? noteText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(bodyLines);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmText);
        ArgumentNullException.ThrowIfNull(onConfirmAsync);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelText);

        _onConfirmAsync = onConfirmAsync;
        _getFocusTarget = getFocusTarget;

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
            Tone = ControlTone.Default,
        };
        closeButton.Click(Close);

        var cancelButton = new Button(cancelText)
        {
            Tone = ControlTone.Default,
        };
        cancelButton.Click(Close);

        var confirmButton = new Button(confirmText)
        {
            Tone = confirmTone,
        };
        confirmButton.Click(() => _ = ConfirmAsync());

        var body = new VStack(
            bodyLines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => (Visual)new TextBlock(line).Wrap(true))
                .ToArray())
        {
            HorizontalAlignment = Align.Stretch,
        };

        var buttonRow = new HStack(cancelButton, confirmButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        Visual bottom = buttonRow;
        if (CreateNote(noteText) is { } note)
        {
            bottom = new VStack(note, buttonRow)
            {
                HorizontalAlignment = Align.Stretch,
                Spacing = 1,
            };
        }

        var content = new DockLayout()
            .Content(new ScrollViewer(body, focusable: false).Stretch())
            .Bottom(bottom)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        _dialog = new Dialog()
            .Title(title)
            .TopRightText(closeButton)
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 48, minHeight: 9, widthFactor: 0.30, heightFactor: 0.30);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ConfirmationDialog.Close",
            LabelMarkup = cancelText,
            DescriptionMarkup = "Close the confirmation dialog.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
    }

    public void Show()
        => _dialog.Show();

    private async Task ConfirmAsync()
    {
        Close();
        await _onConfirmAsync();
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

    private static Visual? CreateNote(string? noteText)
    {
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return null;
        }

        return new Markup($"[dim]{noteText}[/]").Wrap(true);
    }
}
