using System.Text;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Styling;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Logging;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class SidebarView
{
    private const string ExpandNavigatorIcon = "\u25E7";
    private const string CollapseNavigatorIcon = "\u25E8";

    private static readonly ButtonStyle ToolbarButtonStyle = ButtonStyle.Default with
    {
        Padding = new Thickness(1, 0, 1, 0),
    };

    private static readonly ButtonStyle TitleButtonStyle = ButtonStyle.Default with
    {
        Padding = Thickness.Zero,
    };

    private readonly Dictionary<SidebarSelectionTarget, TreeNode> _nodesByTarget = new();
    private readonly Dictionary<string, SidebarNodeHeaderView> _headersByNodeId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISidebarRowCommandDispatcher _rowCommandDispatcher;
    private readonly Action<SidebarNodeViewModel> _submitInlineRename;
    private readonly Action<SidebarNodeViewModel> _cancelInlineRename;
    private readonly Grid _contentGrid;
    private readonly Markup _title;
    private readonly TextBlock _collapseToggleIcon;
    private readonly Group _group;
    private readonly VSplitter _rootSplitter;
    private readonly SidebarNotesView _notesView;
    private bool _isCollapsed;

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
        Action? openLogs = null,
        IAltaNotesService? notesService = null)
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
        Tree.Style(() => UiPalette.GetSidebarTreeStyle(Tree.GetTheme()));
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
                () => TerminalIcons.MdRefresh,
                SR.T("Refresh projects and sessions"),
                refreshCatalog),
            CreateToolbarButton(
                () => viewModel.SortMode == NavigatorProjectSortMode.Name
                    ? TerminalIcons.MdSortAlphabeticalAscending
                    : TerminalIcons.MdSortCalendarDescending,
                () => viewModel.SortMode == NavigatorProjectSortMode.Name
                    ? SR.T("Sort projects by name")
                    : SR.T("Sort projects by last activity"),
                cycleSortMode),
            CreateToolbarButton(
                () => TerminalIcons.MdCogOutline,
                SR.T("Workspace settings"),
                openNavigatorSettings),
        ])
        {
            Spacing = 2,
            HorizontalAlignment = Align.Stretch,
        };
        if (openLogs is not null)
        {
            footer.Children.Add(
                new Button(new TextBlock($"{TerminalIcons.MdTextBoxSearchOutline} {SR.T("Show Logs")}"))
                    .Style(ToolbarButtonStyle)
                    .Click(openLogs)
                    .Tooltip(new TextBlock(SR.T("Show application logs"))));
        }

        _contentGrid = new Grid
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }
            .Rows(
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Star(1) });
        _contentGrid.Cell(treeHost, 0, 0);
        _contentGrid.Cell(footer, 1, 0);

        _title = new Markup(BuildTitleMarkup());
        _collapseToggleIcon = new TextBlock(CollapseNavigatorIcon);
        var collapseToggle = new TitleButton(_collapseToggleIcon)
            .Style(TitleButtonStyle)
            .Click(() => SetCollapsed(!_isCollapsed))
            .Tooltip(new TextBlock(() => _isCollapsed ? SR.T("Expand navigator") : SR.T("Collapse navigator")));

        Group? group = null;
        group = new Group(_title, _contentGrid)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }
        .Style(() => UiPalette.GetSidebarGroupStyle(group!.GetTheme()));
        group.TopRightText = collapseToggle;

        _group = group;
        _notesView = new SidebarNotesView(notesService ?? new AltaNotesService());
        _rootSplitter = new VSplitter
        {
            First = _group,
            Second = _notesView.Root,
            Ratio = 0.75,
            MinFirst = 6,
            MinSecond = 4,
        };
        Root = _rootSplitter;
    }

    public Visual Root { get; }

    public Visual NavigatorRoot => _group;

    public Visual NotesRoot => _notesView.Root;

    public TreeView Tree { get; }

    public bool IsCollapsed => _isCollapsed;

    public event Action<bool>? CollapsedChanged;

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

    public void SetCollapsed(bool isCollapsed)
    {
        if (_isCollapsed == isCollapsed)
        {
            return;
        }

        _isCollapsed = isCollapsed;
        _title.Text = BuildTitleMarkup();
        _collapseToggleIcon.Text = isCollapsed ? ExpandNavigatorIcon : CollapseNavigatorIcon;
        _group.Content = isCollapsed ? null : _contentGrid;
        _rootSplitter.Second = isCollapsed ? null : _notesView.Root;
        CollapsedChanged?.Invoke(isCollapsed);
    }

    public void SetNotesMarkdown(string markdown)
        => _notesView.SetMarkdown(markdown);

    private string BuildTitleMarkup() => _isCollapsed ? "" :
        $"[bold]{TerminalIcons.FaFolderTree} {SR.T("Navigator")}[/]";

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

        if (projection.Kind is SidebarNodeKind.Global or SidebarNodeKind.Project or SidebarNodeKind.Session)
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

    private sealed class SidebarNotesView
    {
        private readonly IAltaNotesService _notesService;
        private readonly MarkdownControl _markdown;

        public SidebarNotesView(IAltaNotesService notesService)
        {
            ArgumentNullException.ThrowIfNull(notesService);

            _notesService = notesService;
            _markdown = new MarkdownControl(ReadInitialNotesMarkdown(notesService))
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                Options = MarkdownRenderOptions.Default with
                {
                    WrapCodeBlocks = true,
                    MaxCodeBlockHeight = 8,
                },
            };

            var copyButton = new Button(new TextBlock(TerminalIcons.MdContentCopy.ToString()) { Wrap = false, IsSelectable = false })
                .Style(TitleButtonStyle);
            copyButton.Click(() => copyButton.App?.Terminal.Clipboard.TrySetText(_markdown.Markdown ?? string.Empty));
            var copyButtonHost = copyButton.Tooltip(new TextBlock(SR.T("Copy notes as Markdown")));
            var clearButton = new Button(new TextBlock($"{TerminalIcons.MdTrashCanOutline} {SR.T("Clear")}") { Wrap = false, IsSelectable = false })
                .Style(TitleButtonStyle);
            clearButton.Click(ClearNotes);
            var clearButtonHost = clearButton.Tooltip(new TextBlock(SR.T("Clear notes")));
            var notesScroll = new ScrollViewer(_markdown)
                .HorizontalScrollEnabled(false)
                .VerticalScrollEnabled(true)
                .Stretch();

            Group? notesGroup = null;
            notesGroup = new Group($"{TerminalIcons.MdNoteTextOutline} {SR.T("Notes")}", notesScroll)
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .TopRightText(copyButtonHost)
            .BottomRightText(clearButtonHost)
            .Style(() => UiPalette.GetSidebarGroupStyle(notesGroup!.GetTheme()));
            Root = notesGroup;
        }

        public Visual Root { get; }

        public void SetMarkdown(string markdown)
        {
            ArgumentNullException.ThrowIfNull(markdown);
            _markdown.Markdown = markdown;
        }

        private void ClearNotes()
            => _ = ClearNotesAsync();

        private async Task ClearNotesAsync()
        {
            try
            {
                await _notesService.ClearAsync(AltaCallerIdentity.Host);
            }
            catch (AltaNotesSessionRequiredException)
            {
            }
            catch (Exception ex)
            {
                CodeAltaApp.UiLogger.Error(ex, "Failed to clear sidebar notes.");
            }
        }

        private static string ReadInitialNotesMarkdown(IAltaNotesService notesService)
        {
            try
            {
                return notesService.GetMarkdown(AltaCallerIdentity.Host);
            }
            catch (AltaNotesSessionRequiredException)
            {
                return string.Empty;
            }
        }
    }

    private sealed class TitleButton : Button
    {
        public TitleButton(Visual content)
           : base(content)
        {
            Focusable = false;
        }
    }

    private Action ResolveRowAction(SidebarSelectionTarget? target, SidebarRowActionKind actionKind)
    {
        return actionKind switch
        {
            SidebarRowActionKind.DeleteSession when target?.SessionId is { } sessionId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.DeleteSession(sessionId)),
            SidebarRowActionKind.DeleteProject when target?.ProjectId is { } projectId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.DeleteProject(projectId)),
            SidebarRowActionKind.OpenProjectSessions when target?.Kind == SidebarSelectionKind.GlobalScope
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenProjectSessions(string.Empty)),
            SidebarRowActionKind.OpenProjectSessions when target?.ProjectId is { } projectId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenProjectSessions(projectId)),
            SidebarRowActionKind.OpenProjectDetails when target?.ProjectId is { } projectId
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenProjectDetails(projectId)),
            SidebarRowActionKind.OpenFolder
                => () => _rowCommandDispatcher.Dispatch(new SidebarRowCommand.OpenFolder()),
            _ => static () => { }
            ,
        };
    }

    private static TreeNodeRightVisualVisibility ResolveRowActionVisibility(SidebarRowActionVisibility visibility)
        => visibility == SidebarRowActionVisibility.Always
            ? TreeNodeRightVisualVisibility.Always
            : TreeNodeRightVisualVisibility.Hover;

    private static ControlTone ResolveRowActionTone(SidebarRowActionKind actionKind)
        => actionKind is SidebarRowActionKind.DeleteSession or SidebarRowActionKind.DeleteProject
            ? ControlTone.Error
            : ControlTone.Default;
}
