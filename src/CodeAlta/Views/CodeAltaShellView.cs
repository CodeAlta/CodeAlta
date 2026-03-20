using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class CodeAltaShellView
{
    public CodeAltaShellView(
        CodeAltaShellViewModel viewModel,
        Visual sidebar,
        Visual threadWorkspace,
        CommandBar threadCommandBar)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(threadWorkspace);
        ArgumentNullException.ThrowIfNull(threadCommandBar);

        var mainLayout = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });

        mainLayout.Cell(
            new HSplitter(sidebar, threadWorkspace)
            {
                Ratio = 0.26,
                MinFirst = 24,
                MinSecond = 40,
            },
            0,
            0);
        mainLayout.Cell(threadCommandBar, 1, 0);

        var root = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });

        root.Cell(
            new TextBlock
            {
                Wrap = false,
            }.Text(() => viewModel.HeaderText),
            0,
            0);
        root.Cell(mainLayout, 1, 0);

        Root = root;
    }

    public Visual Root { get; }
}