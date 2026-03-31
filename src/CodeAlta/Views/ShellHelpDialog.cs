using CodeAlta.Frontend.Help;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
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

    public Task ShowAsync(string? filterText = null)
    {
        if (_dialog is { App: not null })
        {
            return Task.CompletedTask;
        }

        var sections = ShellHelpContentBuilder.BuildSections(filterText);
        var contentItems = new List<Visual>
        {
            new Markup("[bold]Shell Commands[/]").Wrap(true),
            new Markup("[dim]Use ?, /, or the shortcuts below to discover available shell actions.[/]").Wrap(true),
        };

        if (sections.Count == 0)
        {
            contentItems.Add(new Markup("[dim]No commands matched that filter.[/]").Wrap(true));
        }
        else
        {
            foreach (var section in sections)
            {
                contentItems.Add(new Markup($"[bold]{section.Title}[/]").Wrap(true));
                foreach (var entry in section.Entries)
                {
                    var bindingText = entry.Bindings.Count == 0
                        ? string.Empty
                        : $" [dim]({string.Join(" · ", entry.Bindings)})[/]";
                    contentItems.Add(new Markup($"[bold]{entry.Label}[/]{bindingText}").Wrap(true));
                    contentItems.Add(new TextBlock(entry.Description).Wrap(true));
                }
            }
        }

        var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(Close);

        _dialog = new Dialog()
            .Title("Shell Help")
            .TopRightText(closeButton)
            .BottomRightText(new Markup("[dim]Esc Close[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(new VStack(contentItems.ToArray()) { Spacing = 1 }.Scrollable());
        ResponsiveDialogSize.Apply(_dialog, _getBounds(), minWidth: 70, minHeight: 16, widthFactor: 0.72, heightFactor: 0.7);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Shell.Help.Close",
            LabelMarkup = "Close",
            DescriptionMarkup = "Close shell help.",
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
