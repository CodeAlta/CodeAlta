using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.Views;

internal sealed class CodeAltaShellView
{
    private const double DefaultSidebarRatio = 0.26d;
    private const int ExpandedSidebarMinWidth = 24;
    private const int CollapsedSidebarMinWidth = 5;

    private readonly HSplitter _sidebarSplitter;
    private double _expandedSidebarRatio = DefaultSidebarRatio;
    private bool _sidebarCollapsed;

    public CodeAltaShellView(
        Visual sidebar,
        Visual sessionWorkspace,
        Visual sessionCommandBar,
        Action<TerminalApp> configureApp)
    {
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(sessionWorkspace);
        ArgumentNullException.ThrowIfNull(sessionCommandBar);
        ArgumentNullException.ThrowIfNull(configureApp);

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

        _sidebarSplitter = new HSplitter(sidebar, sessionWorkspace)
        {
            Ratio = DefaultSidebarRatio,
            MinFirst = ExpandedSidebarMinWidth,
            MinSecond = 40,
        };
        mainLayout.Cell(_sidebarSplitter, 0, 0);
        mainLayout.Cell(sessionCommandBar, 1, 0);

        Root = new CodeAltaRootView(mainLayout, configureApp)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    public Visual Root { get; }

    internal HSplitter SidebarSplitter => _sidebarSplitter;

    public void SetSidebarCollapsed(bool isCollapsed)
    {
        if (_sidebarCollapsed == isCollapsed)
        {
            return;
        }

        if (isCollapsed)
        {
            _expandedSidebarRatio = Math.Clamp(_sidebarSplitter.Ratio, 0.0d, 1.0d);
            _sidebarSplitter.MinFirst = CollapsedSidebarMinWidth;
            _sidebarSplitter.Ratio = 0.0d;
        }
        else
        {
            _sidebarSplitter.MinFirst = ExpandedSidebarMinWidth;
            _sidebarSplitter.Ratio = Math.Clamp(_expandedSidebarRatio, 0.0d, 1.0d);
        }

        _sidebarCollapsed = isCollapsed;
    }
}
