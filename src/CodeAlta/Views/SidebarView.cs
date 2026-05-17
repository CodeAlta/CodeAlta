using CodeAlta.Catalog;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using System.Text;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class SidebarView
{
    private static readonly ButtonStyle ToolbarButtonStyle = ButtonStyle.Default with
    {
        Padding = new Thickness(1, 0, 1, 0),
    };

    private readonly Dictionary<SidebarSelectionTarget, TreeNode> _nodesByTarget = new();
    private readonly Dictionary<string, SidebarNodeHeaderView> _headersByNodeId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISidebarRowCommandDispatcher _rowCommandDispatcher;
    private readonly Action<SidebarNodeViewModel> _submitInlineRename;
    private readonly Action<SidebarNodeViewModel> _cancelInlineRename;

    public SidebarView(
        SidebarViewModel viewModel,
        Action refreshCatalog,
        Action cycleSortMode,
        Action openNavigatorSettings,
        Action beginInlineRenameSelectedProject,
        Action<SidebarNodeViewModel> submitInlineRename,
        Action<SidebarNodeViewModel> cancelInlineRename,
        ISidebarRowCommandDispatcher rowCommandDispatcher,
        Action<SidebarSelectionTarget?> onSelectedTargetChanged,
        Action? openLogs = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(refreshCatalog);
        ArgumentNullException.ThrowIfNull(cycleSortMode);
        ArgumentNullException.ThrowIfNull(openNavigatorSettings);
        ArgumentNullException.ThrowIfNull(beginInlineRenameSelectedProject);
        ArgumentNullException.ThrowIfNull(submitInlineRename);
        ArgumentNullException.ThrowIfNull(cancelInlineRename);
        ArgumentNullException.ThrowIfNull(rowCommandDispatcher);
        ArgumentNullException.ThrowIfNull(onSelectedTargetChanged);

        _rowCommandDispatcher = rowCommandDispatcher;
        _submitInlineRename = submitInlineRename;
        _cancelInlineRename = cancelInlineRename;

        Tree = new TreeView
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
        Tree.KeyDown((_, _) => onSelectedTargetChanged(SelectedTarget));
        Tree.PointerPressed((_, _) => onSelectedTargetChanged(SelectedTarget));
        Tree.AddCommand(new Command
        {
            Id = "Sidebar.BeginInlineProjectRename",
            LabelMarkup = string.Empty,
            Gesture = new KeyGesture(TerminalKey.F2),
            Presentation = CommandPresentation.None,
            Execute = _ => beginInlineRenameSelectedProject(),
            CanExecute = _ => SelectedTarget?.Kind == SidebarSelectionKind.ProjectScope,
            ConsumesGestureWhenUnavailable = false,
        });

        var treeHost = new ScrollViewer(Tree)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true);

        var footer = new HStack(
        [
            CreateToolbarButton(
                () => NerdFont.MdRefresh,
                "Refresh projects and threads",
                refreshCatalog),
            CreateToolbarButton(
                () => viewModel.SortMode == NavigatorProjectSortMode.Name
                    ? NerdFont.MdSortAlphabeticalAscending
                    : NerdFont.MdSortCalendarDescending,
                () => viewModel.SortMode == NavigatorProjectSortMode.Name
                    ? "Sort projects by name"
                    : "Sort projects by last activity",
                cycleSortMode),
            CreateToolbarButton(
                () => NerdFont.MdCogOutline,
                "Workspace settings",
                openNavigatorSettings),
        ])
        {
            Spacing = 2,
            HorizontalAlignment = Align.Stretch,
        };
        if (openLogs is not null)
        {
            footer.Children.Add(
                new Button(new TextBlock($"{NerdFont.MdTextBoxSearchOutline} Show Logs"))
                    .Style(ToolbarButtonStyle)
                    .Click(openLogs)
                    .Tooltip(new TextBlock("Show application logs")));
        }

        var contentGrid = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
        contentGrid.Cell(treeHost, 0, 0);
        contentGrid.Cell(footer, 1, 0);

        Group? group = null;
        group = new Group(
            new Markup($"[bold]{AnsiMarkup.Escape($"{NerdFont.FaFolderTree} Navigator")}[/]"),
            contentGrid)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }
        .Style(() => UiPalette.GetSidebarGroupStyle(group!.GetTheme()));

        Root = group;
    }

    public Visual Root { get; }

    public TreeView Tree { get; }

    public SidebarSelectionTarget? SelectedTarget
        => Tree.SelectedNode?.Data is SidebarSelectionTarget target ? target : null;

    public void ApplyProjection(SidebarTreeProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        _nodesByTarget.Clear();
        _headersByNodeId.Clear();
        Tree.Roots.Clear();
        foreach (var root in projection.Roots)
        {
            Tree.Roots.Add(CreateNode(root));
        }
    }

    public bool TrySelectTarget(SidebarSelectionTarget target)
    {
        return _nodesByTarget.TryGetValue(target, out var node) &&
               Tree.TrySelectNode(node);
    }

    public IReadOnlyList<string> GetExpandedProjectIds()
    {
        return _nodesByTarget
            .Where(static entry => entry.Key.Kind == SidebarSelectionKind.ProjectScope && entry.Key.ProjectId is not null && entry.Value.IsExpanded)
            .Select(static entry => entry.Key.ProjectId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void FocusInlineRenameEditor(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (_headersByNodeId.TryGetValue(nodeId, out var header))
        {
            header.RequestEditorFocus();
        }
    }

    private TreeNode CreateNode(SidebarTreeNodeProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var header = CreateSidebarHeader(projection.Row, _submitInlineRename, _cancelInlineRename);
        _headersByNodeId[projection.NodeId] = header;

        var node = new TreeNode(header)
        {
            Icon = projection.Icon,
            IconStyle = UiPalette.GetSidebarIconStyle(projection.Accent),
            Data = projection.SelectionTarget,
            IsExpanded = projection.IsExpanded,
        };

        if (projection.Kind is SidebarNodeKind.Global or SidebarNodeKind.Project or SidebarNodeKind.Thread)
        {
            node.AddRightVisual(CreateTimestampVisual(projection.Row), TreeNodeRightVisualVisibility.Always);
        }

        foreach (var action in projection.Actions)
        {
            node.AddRightVisual(
                CreateRowActionVisual(
                    projection.Row,
                    action.Icon,
                    action.Tooltip,
                    ResolveRowActionTone(action.Kind),
                    ResolveRowAction(projection.SelectionTarget, action.Kind)),
                ResolveRowActionVisibility(action.Visibility));
        }

        if (projection.SelectionTarget is { } target)
        {
            _nodesByTarget[target] = node;
        }

        foreach (var child in projection.Children)
        {
            node.Children.Add(CreateNode(child));
        }

        return node;
    }

    private static Visual CreateTimestampVisual(SidebarNodeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new Markup(() =>
                $"[{UiPalette.MutedMarkup}]{AnsiMarkup.Escape(row.RelativeActivityText)}[/]")
            .Wrap(false)
            .TextAlignment(TextAlignment.Right)
            .MinWidth(12)
            .Tooltip(new TextBlock(() => row.ExactActivityText));
    }

    private static Visual CreateRowActionVisual(
        SidebarNodeViewModel row,
        Rune icon,
        string tooltip,
        ControlTone tone,
        Action onClick)
    {
        ArgumentNullException.ThrowIfNull(row);

        var button = CreateRowActionButton(icon, tooltip, tone, onClick);
        return new ComputedVisual(() => row.IsInlineEditing ? null : button);
    }

    private static Visual CreateRowActionButton(Rune icon, string tooltip, ControlTone tone, Action onClick)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltip);
        ArgumentNullException.ThrowIfNull(onClick);

        return new Button(new TextBlock(icon.ToString()))
            .Style(ToolbarButtonStyle)
            .Tone(tone)
            .Click(onClick)
            .Tooltip(new TextBlock(tooltip));
    }

    private static Visual CreateToolbarButton(
        Func<Rune> iconFactory,
        string tooltip,
        Action onClick)
    {
        ArgumentNullException.ThrowIfNull(iconFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tooltip);
        ArgumentNullException.ThrowIfNull(onClick);

        return new Button(new TextBlock(() => iconFactory().ToString()))
            .Style(ToolbarButtonStyle)
            .Click(onClick)
            .Tooltip(new TextBlock(tooltip));
    }

    private static Visual CreateToolbarButton(
        Func<Rune> iconFactory,
        Func<string> tooltipFactory,
        Action onClick)
    {
        ArgumentNullException.ThrowIfNull(iconFactory);
        ArgumentNullException.ThrowIfNull(tooltipFactory);
        ArgumentNullException.ThrowIfNull(onClick);

        return new Button(new TextBlock(() => iconFactory().ToString()))
            .Style(ToolbarButtonStyle)
            .Click(onClick)
            .Tooltip(new TextBlock(tooltipFactory));
    }

    private static SidebarNodeHeaderView CreateSidebarHeader(
        SidebarNodeViewModel row,
        Action<SidebarNodeViewModel> submitInlineRename,
        Action<SidebarNodeViewModel> cancelInlineRename)
        => new SidebarNodeHeaderView(row, submitInlineRename, cancelInlineRename);

    private Action ResolveRowAction(SidebarSelectionTarget? target, SidebarRowActionKind actionKind)
    {
        return actionKind switch
        {
            SidebarRowActionKind.DeleteThread when target?.ThreadId is { } threadId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.DeleteThread(threadId)),
            SidebarRowActionKind.DeleteProject when target?.ProjectId is { } projectId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.DeleteProject(projectId)),
            SidebarRowActionKind.OpenProjectThreads when target?.Kind == SidebarSelectionKind.GlobalScope
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenProjectThreads(string.Empty)),
            SidebarRowActionKind.OpenProjectThreads when target?.ProjectId is { } projectId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenProjectThreads(projectId)),
            SidebarRowActionKind.OpenProjectDetails when target?.ProjectId is { } projectId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenProjectDetails(projectId)),
            SidebarRowActionKind.OpenFolder
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenFolder()),
            _ => static () => { },
        };
    }

    private static TreeNodeRightVisualVisibility ResolveRowActionVisibility(SidebarRowActionVisibility visibility)
        => visibility == SidebarRowActionVisibility.Always
            ? TreeNodeRightVisualVisibility.Always
            : TreeNodeRightVisualVisibility.Hover;

    private static ControlTone ResolveRowActionTone(SidebarRowActionKind actionKind)
        => actionKind is SidebarRowActionKind.DeleteThread or SidebarRowActionKind.DeleteProject
            ? ControlTone.Error
            : ControlTone.Default;
}
