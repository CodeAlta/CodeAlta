using CodeAlta.Catalog;
using CodeAlta.Frontend.Commands;
using CodeAlta.Frontend.Help;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal sealed class ShellHelpDialog
{
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private Dialog? _dialog;

    public ShellHelpDialog(Func<Rectangle?> getBounds, Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
    }

    public Task ShowAsync(IReadOnlyList<ShellCommand> commands, string? filterText = null)
    {
        if (_dialog is { App: not null })
        {
            return Task.CompletedTask;
        }

        var markdown = ShellHelpContentBuilder.BuildMarkdown(commands, filterText);

        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(Close);

        _dialog = new Dialog()
            .Title(SR.T("Shell Help"))
            .TopRightText(closeButton)
            .BottomRightText(new Markup($"[dim]{SR.T("Esc")} {SR.T("Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(new MarkdownControl(markdown)
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                Options = MarkdownRenderOptions.Default with
                {
                    WrapCodeBlocks = true,
                    MaxCodeBlockHeight = 12,
                },
            });
        ResponsiveDialogSize.Apply(_dialog, _getBounds(), minWidth: 70, minHeight: 16, widthFactor: 0.72, heightFactor: 0.7);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Shell.Help.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close shell help."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });

        _dialog.Show();
        return Task.CompletedTask;
    }

    private void Close()
    {
        var dialog = _dialog;
        _dialog = null;
        var app = dialog?.App;
        dialog?.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }
}
