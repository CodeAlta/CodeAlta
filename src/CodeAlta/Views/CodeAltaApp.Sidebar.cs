using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;

internal sealed partial class CodeAltaApp
{
    private CodeAltaShellView EnsureShellView()
    {
        _sidebarView ??= new SidebarView(
            _sidebarViewModel,
            () => _ = _shellController.ReloadCatalogAsync(CancellationToken.None));
        _sidebarTree ??= _sidebarView.Tree;

        _threadWorkspaceView ??= new ThreadWorkspaceView(
            _shellViewModel,
            _threadWorkspaceViewModel,
            _promptComposerViewModel,
            _statusSpinner!,
            () => CreateComputedVisual(EnsureSessionUsagePresenter().BuildIndicatorVisual),
            () => _statusTone,
            CreatePromptEditor,
            () => _ = SendSelectedThreadPromptAsync(steer: false),
            OnThreadTabControlSelectionChanged,
            OnChatBackendSelectionChanged,
            OnChatModelSelectionChanged,
            OnChatReasoningSelectionChanged,
            OnChatAutoScrollChanged);
        _threadPaneLayout ??= _threadWorkspaceView.ThreadPaneLayout;
        _threadBottomPanel ??= _threadWorkspaceView.ThreadBottomPanel;
        _threadBodySplitter ??= _threadWorkspaceView.ThreadBodySplitter;
        _threadInput ??= _threadWorkspaceView.ThreadInput;
        _threadInputView ??= _threadWorkspaceView.ThreadInputView;
        _sendPromptButton ??= _threadWorkspaceView.SendPromptButton;
        _threadCommandBar ??= _threadWorkspaceView.ThreadCommandBar;
        _chatBackendSelect ??= _threadWorkspaceView.ChatBackendSelect;
        _chatModelSelect ??= _threadWorkspaceView.ChatModelSelect;
        _chatReasoningSelect ??= _threadWorkspaceView.ChatReasoningSelect;
        _chatAutoScrollCheckBox ??= _threadWorkspaceView.ChatAutoScrollCheckBox;
        _threadTabControl ??= _threadWorkspaceView.ThreadTabControl;

        RebuildSidebarTree();
        RefreshThreadPaneContent();

        _shellView ??= new CodeAltaShellView(
            _shellViewModel,
            _sidebarView.Root,
            _threadWorkspaceView.Root,
            _threadCommandBar);
        return _shellView;
    }

    private void RebuildSidebarTree()
    {
        if (_sidebarTree is null)
        {
            return;
        }

        var roots = new List<TreeNode>
        {
            CreateSidebarGlobalNode(),
            CreateSidebarProjectsNode(),
        };

        _sidebarSelectionSyncEnabled = false;
        try
        {
            _sidebarTree.Roots.Clear();
            foreach (var root in roots)
            {
                _sidebarTree.Roots.Add(root);
            }

            SelectSidebarNodeForCurrentState();
        }
        finally
        {
            _sidebarSelectionSyncEnabled = true;
        }
    }

    private TreeNode CreateSidebarGlobalNode()
    {
        var globalNode = new TreeNode(CreateSidebarHeader(
            "Global",
            _catalogOptions.GlobalRoot))
        {
            Icon = NerdFont.MdHomeOutline,
            IconStyle = UiPalette.GetSidebarIconStyle(SidebarAccent.Global),
            Data = SidebarSelectionTarget.Global(),
            IsExpanded = true,
        };

        foreach (var thread in _threads
                     .Where(static item => item.Kind == WorkThreadKind.GlobalThread)
                     .OrderByDescending(static item => item.LastActiveAt)
                     .Take(MaxRecentThreadsPerProject))
        {
            globalNode.Children.Add(CreateThreadNode(thread));
        }

        return globalNode;
    }

    private TreeNode CreateSidebarProjectsNode()
    {
        var projectsNode = new TreeNode(CreateSidebarHeader(
            "Projects",
            $"{_projects.Count} known projects"))
        {
            Icon = NerdFont.MdFolderMultipleOutline,
            IconStyle = UiPalette.GetSidebarIconStyle(SidebarAccent.Projects),
            IsExpanded = true,
        };

        foreach (var project in _projects.OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            projectsNode.Children.Add(CreateProjectNode(project));
        }

        return projectsNode;
    }

    private TreeNode CreateProjectNode(ProjectDescriptor project)
    {
        var threads = FilterThreadsForProject(_threads, project.Id, includeInternal: true)
            .Take(MaxRecentThreadsPerProject)
            .ToArray();
        var projectNode = new TreeNode(CreateSidebarHeader(
            project.DisplayName,
            project.ProjectPath))
        {
            Icon = NerdFont.MdFolderOutline,
            IconStyle = UiPalette.GetSidebarIconStyle(SidebarAccent.Projects),
            Data = SidebarSelectionTarget.Project(project.Id),
            IsExpanded = string.Equals(project.Id, GetExpandedSidebarProjectId(), StringComparison.OrdinalIgnoreCase),
        };

        foreach (var thread in threads)
        {
            projectNode.Children.Add(CreateThreadNode(thread));
        }

        return projectNode;
    }

    private TreeNode CreateThreadNode(WorkThreadDescriptor thread)
    {
        var icon = thread.Kind switch
        {
            WorkThreadKind.GlobalThread => NerdFont.MdHomeOutline,
            WorkThreadKind.ProjectThread => NerdFont.MdChatProcessingOutline,
            WorkThreadKind.InternalThread => NerdFont.MdAccountCogOutline,
            _ => NerdFont.MdChatProcessingOutline,
        };

        return new TreeNode(CreateSidebarHeader(
            CompactSidebarThreadTitle(thread.Title),
            BuildThreadSidebarTooltip(thread)))
        {
            Icon = icon,
            IconStyle = ResolveSidebarThreadIconStyle(thread.BackendId, thread.Kind),
            Data = SidebarSelectionTarget.Thread(thread.ThreadId),
        };
    }

    private static Visual CreateSidebarHeader(string title, string? tooltip)
    {
        var markup = new Markup($"[bold]{AnsiMarkup.Escape(title)}[/]")
        {
            Wrap = false,
        };

        if (string.IsNullOrWhiteSpace(tooltip))
        {
            return markup;
        }

        return markup.Tooltip(tooltip);
    }

    internal static SidebarAccent ResolveSidebarThreadAccent(string? backendId, WorkThreadKind kind)
    {
        if (string.Equals(backendId, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase))
        {
            return SidebarAccent.CopilotThread;
        }

        return kind switch
        {
            WorkThreadKind.GlobalThread => SidebarAccent.Global,
            WorkThreadKind.ProjectThread => SidebarAccent.ProjectThread,
            WorkThreadKind.InternalThread => SidebarAccent.InternalThread,
            _ => SidebarAccent.Fallback,
        };
    }

    private static Style ResolveSidebarThreadIconStyle(string? backendId, WorkThreadKind kind)
    {
        return UiPalette.GetSidebarIconStyle(ResolveSidebarThreadAccent(backendId, kind));
    }

    private static SidebarSelectionTarget? GetSelectedSidebarTarget(TreeNode? node)
    {
        return node?.Data as SidebarSelectionTarget;
    }

    private string? GetExpandedSidebarProjectId()
    {
        return GetSelectedThread()?.ProjectRef ?? _selectedProjectId;
    }

    private SidebarSelectionTarget ResolveSidebarTargetForCurrentState()
    {
        if (!string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            return SidebarSelectionTarget.Thread(_selectedThreadId);
        }

        if (_globalScopeSelected || string.IsNullOrWhiteSpace(_selectedProjectId))
        {
            return SidebarSelectionTarget.Global();
        }

        return SidebarSelectionTarget.Project(_selectedProjectId);
    }

    private void SelectSidebarNodeForCurrentState()
    {
        if (_sidebarTree is null)
        {
            return;
        }

        _pendingSidebarSelectionTarget = ResolveSidebarTargetForRebuild();
    }

    private void ApplyPendingSidebarSelection()
    {
        if (_sidebarTree is null || _pendingSidebarSelectionTarget is not { } target)
        {
            return;
        }

        var selectedNode = FindSidebarNode(_sidebarTree.Roots, target);
        if (selectedNode is null)
        {
            _pendingSidebarSelectionTarget = null;
            return;
        }

        if (!_sidebarTree.TrySelectNode(selectedNode))
        {
            return;
        }

        _lastSidebarSelectedTarget = target;
        _pendingSidebarSelectionTarget = null;
    }

    private SidebarSelectionTarget ResolveSidebarTargetForRebuild()
    {
        if (_lastSidebarSelectedTarget is { } previousTarget &&
            _sidebarTree is not null &&
            FindSidebarNode(_sidebarTree.Roots, previousTarget) is not null)
        {
            return previousTarget;
        }

        return ResolveSidebarTargetForCurrentState();
    }

    private static TreeNode? FindSidebarNode(IEnumerable<TreeNode> roots, SidebarSelectionTarget selectedTarget)
    {
        foreach (var root in roots)
        {
            if (FindSidebarNode(root, selectedTarget) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static TreeNode? FindSidebarNode(TreeNode node, SidebarSelectionTarget selectedTarget)
    {
        if (node.Data is SidebarSelectionTarget target && target == selectedTarget)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindSidebarNode(child, selectedTarget) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private void SyncSidebarSelection()
    {
        if (!_sidebarSelectionSyncEnabled || _sidebarTree is null || _pendingSidebarSelectionTarget is not null)
        {
            return;
        }

        var target = GetSelectedSidebarTarget(_sidebarTree.SelectedNode);
        if (target is null || target == _lastSidebarSelectedTarget)
        {
            return;
        }

        _lastSidebarSelectedTarget = target;
        switch (target.Kind)
        {
            case SidebarSelectionKind.GlobalScope:
                SelectGlobalScope();
                break;
            case SidebarSelectionKind.ProjectScope when target.ProjectId is not null:
                SelectProjectScope(target.ProjectId);
                break;
            case SidebarSelectionKind.Thread when target.ThreadId is not null:
                OpenThread(target.ThreadId);
                break;
        }
    }

    internal static string CompactSidebarThreadTitle(string title)
    {
        const int maxLength = 34;
        var normalized = title.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(1, maxLength - 1)].TrimEnd() + "…";
    }

    internal static string BuildThreadSidebarTooltip(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (string.IsNullOrWhiteSpace(thread.LatestSummary))
        {
            return thread.Title;
        }

        return $"{thread.Title}\n\n{thread.LatestSummary}";
    }
}
