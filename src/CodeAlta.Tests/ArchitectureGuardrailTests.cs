using System.IO;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ArchitectureGuardrailTests
{
    [TestMethod]
    public void CodeAltaSource_DoesNotContainLegacyUiThreadHelpersOrBroadRefreshView()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();

        AssertSourceDoesNotContain(sourceFiles, "PostToUi");
        AssertSourceDoesNotContain(sourceFiles, "ReadUiValue");
        AssertSourceDoesNotContain(sourceFiles, "RunOnUiThread");
        AssertSourceDoesNotContain(sourceFiles, "RefreshView(");
        AssertSourceDoesNotContain(sourceFiles, "ThreadTabState");
    }

    [TestMethod]
    public void RuntimeEventPump_IsOnlyCodeAltaConsumerOfRuntimeEventStream()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var streamEventMatches = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                File = file,
                Content = File.ReadAllText(file),
            })
            .Where(static entry => entry.Content.Contains("StreamEventsAsync(", StringComparison.Ordinal))
            .Select(static entry => Path.GetFileName(entry.File))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "RuntimeEventPump.cs" },
            streamEventMatches);
    }

    [TestMethod]
    public void FrontendUiFlows_DoNotUseConfigureAwaitFalseOutsideExplicitBackgroundBoundaries()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var disallowedMatches = Directory
            .EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => new
            {
                File = file,
                RelativePath = Path.GetRelativePath(codeAltaRoot, file).Replace('\\', '/'),
                Content = File.ReadAllText(file),
            })
            .Where(static entry => entry.Content.Contains("ConfigureAwait(false)", StringComparison.Ordinal))
            .Where(static entry =>
                entry.RelativePath.StartsWith("Views/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Frontend/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("Presentation/", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("App/", StringComparison.Ordinal) &&
                entry.RelativePath is not
                    "App/AcpAgentRegistryService.cs" and
                    not
                    "App/ChatBackendInitializationCoordinator.cs" and
                    not "App/CodeAltaOwnedServices.cs" and
                    not "App/CodeAltaShellController.cs" and
                    not "App/KnownProjectImporter.cs" and
                    not "App/RuntimeEventPump.cs" and
                    not "App/ShellCatalogStateCoordinator.cs" and
                    not "App/ThreadPromptDraftPersistenceCoordinator.cs" and
                    not "App/ThreadViewStateCoordinator.cs" and
                    not "App/UiTaskDiagnostics.cs")
            .Select(static entry => entry.RelativePath)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), disallowedMatches);
    }

    [TestMethod]
    public void ShellController_DoesNotReferenceTimelineOrDialogPresentationTypes()
    {
        var controllerSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaShellController.cs"));

        Assert.IsFalse(controllerSource.Contains("ThreadTimelinePresenter", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("ToolCallPresenter", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("SessionUsagePresenter", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("DocumentFlow", StringComparison.Ordinal));
        Assert.IsFalse(controllerSource.Contains("Dialog", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_PartialBucketsDoNotReintroduceBridgeOrSettingsSlices()
    {
        var viewsRoot = Path.Combine(GetCodeAltaSourceRoot(), "Views");

        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.ControllerBridge.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.Settings.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.Usage.cs")));
    }

    [TestMethod]
    public void CodeAltaApp_DoesNotOwnDirectTabOrCommandControlFields()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsFalse(appSource.Contains("Dictionary<string, TabPage>", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_threadTabControl", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_sendPromptButton", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatAutoScrollCheckBox", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatBackendSelect", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatModelSelect", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_chatReasoningSelect", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_statusSpinner", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarDraftTitleEditor_IsRemoved()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();

        AssertSourceDoesNotContain(sourceFiles, "DraftThreadTitle");
        AssertSourceDoesNotContain(sourceFiles, "Thread Title (optional)");
    }

    [TestMethod]
    public void UiProjectionAndUsageFiles_KeepExplicitBindableAccessGuards()
    {
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "SidebarCoordinator.cs"));
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(sidebarSource.Contains("verifyBindableAccess();", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("private T ReadBindableState<T>(Func<T> read)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadingAbstractions_MoveOutsideAppNamespace()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();

        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Threading", "IUiDispatcher.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Threading", "UiDispatch.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Threading", "TerminalUiDispatcher.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "IUiDispatcher.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "UiDispatch.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "TerminalUiDispatcher.cs")));
    }

    [TestMethod]
    public void AppOwnedStateAndSharedViews_MoveOutOfLegacyLocations()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();

        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "App", "State", "OpenThreadState.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Models", "OpenThreadState.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "App", "SidebarCoordinator.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Views", "SidebarCoordinator.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Presentation", "Controls", "AnchoredPopupView.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Views", "AnchoredPopupView.cs")));
    }

    [TestMethod]
    public void CodeAltaAppPresentationSlice_IsDeletedAndShellHelpersStayExtracted()
    {
        var viewsRoot = Path.Combine(GetCodeAltaSourceRoot(), "Views");
        var appSource = File.ReadAllText(Path.Combine(viewsRoot, "CodeAltaApp.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(viewsRoot, "CodeAltaApp.Presentation.cs")));
        Assert.IsFalse(appSource.Contains("internal static string BuildHeaderText(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildDraftPromptMessage(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildDraftTabTitle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildWelcomeSubtitle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static IReadOnlyList<string> BuildWelcomeGuidanceLines(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static Visual BuildWelcomePane(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildReadyStatusText(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildThinkingStatusText(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildStatusIconMarkup(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static TextBlockStyle BuildStatusTextStyle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string CompactTabTitle(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static OpenTabIndicatorKind ResolveOpenTabIndicatorKind(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal enum StatusTone", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal readonly record struct StatusSnapshot", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal enum OpenTabIndicatorKind", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal sealed record InitialThreadSelection", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static StatusSnapshot ResolveSelectionStatus(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static InitialThreadSelection ResolveInitialSelection(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static string BuildThreadScopeSummary(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static IReadOnlyList<WorkThreadDescriptor> FilterThreadsForProject(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaAppPresentationSlice_DelegatesSelectorAndPromptAvailabilityWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_chatSelectorCoordinator.RefreshForDraftScope", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_chatSelectorCoordinator.OnBackendSelectionChanged", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_chatSelectorCoordinator.GetPreferredBackendId()", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private PromptComposerProjection BuildPromptComposerProjection(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaAppPresentationSlice_DelegatesTabStripWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_threadTabStripCoordinator.SyncControl()", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadTabStripCoordinator.OnSelectionChanged", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private ThreadTabStripProjection BuildThreadTabStripProjection(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private List<TabPage> BuildDesiredThreadTabPages(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private TabPage EnsureThreadTabPage(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private TabPage EnsureDraftTabPage(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesThreadHistoryWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_threadHistoryCoordinator.EnsureLoadedAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static ThreadHistoryLoadPlan CreateInitialThreadHistoryLoadPlan(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static int FindInitialThreadHistoryStartIndex(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static int CountRenderableHistoryMessages(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task LoadEarlierThreadHistoryAsync(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesRuntimeEventWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var runtimeSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadRuntimeEventCoordinator.cs"));

        Assert.IsTrue(appSource.Contains("_threadRuntimeEventCoordinator.ApplyRuntimeEvent", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ThreadRuntimeEventCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(runtimeSource.Contains("new ThreadRuntimeStateReducer()", StringComparison.Ordinal));
        Assert.IsTrue(runtimeSource.Contains("new ThreadRuntimeTimelineRenderer(", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("SessionUsageAggregator.Merge(", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("tab.Timeline.UpsertInteraction(", StringComparison.Ordinal));
        Assert.IsFalse(runtimeSource.Contains("tab.Timeline.AddStatus(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static bool ShouldPromoteAgentEventToThinking(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static bool ShouldRefreshShellChromeAfterRuntimeEvent(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void UpdateThreadFromAgentEvent(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void UpdateThreadSummary(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private static string SummarizeThreadContent(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesThreadCommandWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var creationSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadCreationCoordinator.cs"));
        var shellCommandSurfaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandSurfaceCoordinator.cs"));
        var threadCommandSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadCommandCoordinator.cs"));
        var executionOptionsSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadExecutionOptionsFactory.cs"));

        Assert.IsTrue(appSource.Contains("CodeAltaFrontendComposition.Create(", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_shellCommandSurfaceCoordinator.SubmitCurrentPromptAsync", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_shellCommandSurfaceCoordinator.SubmitCurrentDelegationAsync", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ThreadCommandCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(shellCommandSurfaceSource.Contains("new ShellInputCoordinator(", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("GetThreadInput()", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("/help", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("AgentPermissionRequest", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("AgentUserInputRequest", StringComparison.Ordinal));
        Assert.IsFalse(threadCommandSource.Contains("new WorkThreadExecutionOptions", StringComparison.Ordinal));
        Assert.IsTrue(creationSource.Contains("_buildPreferredExecutionOptions(", StringComparison.Ordinal));
        Assert.IsTrue(executionOptionsSource.Contains("BuildDelegationExecutionOptions(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<AgentPermissionDecision> HandleThreadPermissionRequestAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<AgentUserInputResponse> HandleThreadUserInputRequestAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private WorkThreadExecutionOptions BuildExecutionOptions(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private WorkThreadExecutionOptions BuildPreferredExecutionOptions(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<WorkThreadDescriptor?> CreateGlobalThreadAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task<WorkThreadDescriptor?> CreateProjectThreadAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private static string CreateTransientThreadKey(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private string ResolveWorkingDirectory(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private IReadOnlyList<string> ResolveProjectRoots(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesThreadStateWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var threadStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ShellThreadStateCoordinator.cs"));

        Assert.IsTrue(appSource.Contains("CodeAltaFrontendComposition.Create(", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.LoadInitialCatalogStateAsync", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("TryResolveInitialCatalogState(cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.ApplyRecoveredCatalogState", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.EnsureThreadTab", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("new ShellThreadStateCoordinator(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("new ShellThreadStateCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(threadStateSource.Contains("new ShellCatalogStateCoordinator(", StringComparison.Ordinal));
        Assert.IsFalse(threadStateSource.Contains("_projectCatalog.LoadAsync", StringComparison.Ordinal));
        Assert.IsFalse(threadStateSource.Contains("_threadCatalog.LoadInternalAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly Dictionary<string, OpenThreadState>", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly ShellSelectionState", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private IReadOnlyList<ProjectDescriptor>", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private IReadOnlyList<WorkThreadDescriptor>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesWorkspaceRefreshWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_workspaceCoordinator.RefreshShellChrome", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_workspaceCoordinator.SetThreadStatus", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_workspaceCoordinator.CreateComputedVisual", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly State<int> _viewRefreshState", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private readonly State<int> _usageRefreshState", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void RefreshThreadPaneContent(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private void SyncSelectedSessionUsageViewModel(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShellWorkspaceCoordinator_RefreshSelectionRebuildsSidebarProjection()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ShellWorkspaceCoordinator.cs"));

        Assert.IsTrue(workspaceSource.Contains("_workspaceContext.RefreshSidebarProjection();", StringComparison.Ordinal));
        Assert.IsFalse(workspaceSource.Contains("_workspaceContext.SyncSidebarSelectionToCurrentState();", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesBackendInitializationWorkflow()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("_chatBackendInitializationCoordinator.InitializeAsync", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("private async Task RefreshChatBackendStateAsync(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("internal static (ChatBackendAvailability Availability, string StatusMessage) ClassifyBackendInitializationFailure(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DelegatesTerminalLoopLifecycle()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));
        var loopSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "TerminalLoopCoordinator.cs"));
        var deferredAppSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "DeferredCodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("internal TerminalLoopResult Tick(CancellationToken cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_terminalLoopCoordinator.OnIteration(cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("ToggleTerminalLoopCallback()", StringComparison.Ordinal));
        Assert.IsTrue(deferredAppSource.Contains("Terminal.RunAsync(", StringComparison.Ordinal));
        Assert.IsTrue(deferredAppSource.Contains("_app.Tick(cancellationToken)", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_terminalLoopStarted", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_shellController.AttachUiDispatcher(_uiDispatcher)", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("_runtimeEventPump.Start(cancellationToken)", StringComparison.Ordinal));
        Assert.IsFalse(loopSource.Contains("DrainPendingRuntimeEvents()", StringComparison.Ordinal));
        Assert.IsFalse(loopSource.Contains("_syncSidebarSelection();", StringComparison.Ordinal));
        Assert.IsTrue(loopSource.Contains("TerminalLoopResult.Continue", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WelcomePaneFactory_KeepsAnimatedAltaGradientStyle()
    {
        var welcomeSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Shell", "WelcomePaneFactory.cs"));

        Assert.IsTrue(welcomeSource.Contains(".Style(() => BuildWelcomeAltaFigletStyle(welcomeAnimationPhase01.Value))", StringComparison.Ordinal));
        Assert.IsFalse(welcomeSource.Contains("DateTime.UtcNow.Ticks", StringComparison.Ordinal));
        Assert.IsFalse(welcomeSource.Contains("private static readonly TextFigletStyle WelcomeAltaFigletStyle", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_DoesNotConstructPromptEditorControls()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));

        Assert.IsFalse(appSource.Contains("private ChatPromptEditor CreatePromptEditor(", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("new ChatPromptEditor(", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("private static ChatPromptEditor CreatePromptEditor(", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("new ChatPromptEditor(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadWorkspaceView_ExpandedPromptDialog_UsesEscapeCommandWithoutDirectKeyHandler()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));

        Assert.IsTrue(workspaceSource.Contains("editor.AddCommand(CreateExpandedPromptDialogCloseCommand());", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("dialog.AddCommand(CreateExpandedPromptDialogCloseCommand());", StringComparison.Ordinal));
        Assert.IsFalse(workspaceSource.Contains("dialog.KeyDown(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadWorkspaceView_ExpandedPromptDialog_TransfersFocusOnOpenAndClose()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));

        Assert.IsTrue(workspaceSource.Contains("dialog.App?.Focus(editor);", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("app?.Focus(ThreadInput);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadWorkspaceView_BottomBar_RightAlignsPromptActions_AndSendBecomesAbort()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));
        var normalizedSource = workspaceSource.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.IsTrue(workspaceSource.Contains("SendPromptButton = CreatePromptActionButton(promptComposerViewModel, sendPrompt, abortThread);", StringComparison.Ordinal));
        Assert.IsTrue(normalizedSource.Contains("usageIndicator,\n            threadInfoButton,\n            ExpandPromptButton,\n            SendPromptButton,", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("var icon = isAbort ? $\"{NerdFont.MdSquare}\" : $\"{NerdFont.MdSend}\";", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("var tone = isAbort ? ControlTone.Error : ControlTone.Success;", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("var tooltipText = isAbort ? \"Abort the selected thread run.\" : \"Send the current prompt.\";", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadDraftPersistence_UsesMachineSavedPromptsAndDeleteHooks()
    {
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var promptDraftSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "PromptDraftUiCoordinator.cs"));
        var persistenceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadPromptDraftPersistenceCoordinator.cs"));
        var threadStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ShellThreadStateCoordinator.cs"));

        Assert.IsTrue(compositionSource.Contains("callbacks.LoadPromptDraft", StringComparison.Ordinal));
        Assert.IsTrue(compositionSource.Contains("callbacks.DeletePromptDraft", StringComparison.Ordinal));
        Assert.IsTrue(promptDraftSource.Contains("_promptDraftPersistence.ObservePromptDraft", StringComparison.Ordinal));
        Assert.IsTrue(persistenceSource.Contains("saved_prompts", StringComparison.Ordinal));
        Assert.IsTrue(persistenceSource.Contains("saved_prompt_", StringComparison.Ordinal));
        Assert.IsTrue(threadStateSource.Contains("_deletePromptDraft(threadId);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadActivityIndicators_UseEditedDraftStateAndDotsSpinners()
    {
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ThreadWorkspaceView.cs"));
        var sidebarHeaderSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarNodeHeaderView.cs"));
        var tabSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Tabs", "ThreadTabVisualFactory.cs"));
        var statusSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Shell", "StatusVisualFormatter.cs"));

        Assert.IsTrue(workspaceSource.Contains("new Spinner().Style(SpinnerStyles.Dots)", StringComparison.Ordinal));
        Assert.IsTrue(sidebarHeaderSource.Contains("new Spinner().Style(SpinnerStyles.Dots)", StringComparison.Ordinal));
        Assert.IsTrue(tabSource.Contains("OpenTabIndicatorKind.Edited", StringComparison.Ordinal));
        Assert.IsTrue(tabSource.Contains("new Spinner().Style(SpinnerStyles.Dots)", StringComparison.Ordinal));
        Assert.IsTrue(statusSource.Contains("BuildPromptEditedStatusText", StringComparison.Ordinal));
        Assert.IsTrue(statusSource.Contains("BuildPromptEditedIconMarkup", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProjectThreadsDialog_ActionColumn_UsesDirectActivateButtonEditor()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ProjectThreadsDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🧵 Thread\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🤖 Provider\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🕒 Updated\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"💬 Messages\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Header = new TextBlock(\"🚀 Open\")", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("var row = (ProjectThreadsDialogRowViewModel)value.GetBinding().Owner;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("return new TextBlock(() => row.LastUpdatedRelative)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Tooltip(new TextBlock(() => row.LastUpdatedExact));", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellTemplate = new DataTemplate<string>(BuildBackendCell, null)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("SidebarThreadPresentation.BuildProviderMarkup(row.BackendId, row.BackendDisplayName, row.ThreadKind)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".FilterRowVisible(_viewModel.Bind.FilterRowVisible)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("new CheckBox(\"Filter row\").IsChecked(_viewModel.Bind.FilterRowVisible)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellActivationMode = DataGridCellActivationMode.DirectActivate", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellTemplate = new DataTemplate<string>(BuildOpenButtonDisplay, null)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("CellEditorTemplate = new DataTemplate<string>(null, BuildOpenButtonEditor)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("IsHitTestVisible = false", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("BindingAccessor<ProjectThreadsDialogRowViewModel>", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("TypedValueAccessor = rowAccessor", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("ReadOnly = true", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("Show Filter", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("Hide Filter", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarGlobalScope_CanOpenGlobalThreadsDialog()
    {
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarView.cs"));
        var navigatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "NavigatorActionCoordinator.cs"));

        Assert.IsTrue(sidebarSource.Contains("SidebarRowActionKind.OpenProjectThreads when target?.Kind == SidebarSelectionKind.GlobalScope", StringComparison.Ordinal));
        Assert.IsTrue(navigatorSource.Contains("if (string.IsNullOrWhiteSpace(projectId))", StringComparison.Ordinal));
        Assert.IsTrue(navigatorSource.Contains("thread.Kind == WorkThreadKind.GlobalThread", StringComparison.Ordinal));
        Assert.IsTrue(navigatorSource.Contains("CreateGlobalDialogProject()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarCoordinator_PreservesMultipleExpandedProjectsAcrossRefresh()
    {
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "SidebarCoordinator.cs"));
        var sidebarViewSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarView.cs"));

        Assert.IsTrue(sidebarSource.Contains("var expandedProjectIds = new HashSet<string>(_view.GetExpandedProjectIds(), StringComparer.OrdinalIgnoreCase);", StringComparison.Ordinal));
        Assert.IsTrue(sidebarSource.Contains("expandedProjectIds.Add(preferredExpandedProjectId);", StringComparison.Ordinal));
        Assert.IsTrue(sidebarViewSource.Contains("public IReadOnlyList<string> GetExpandedProjectIds()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_UsesListBoxAndDedicatedViewModel()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "ViewModels", "ModelProviderEditorItemViewModel.cs"));

        Assert.IsTrue(dialogSource.Contains("new ListBox<ModelProviderEditorItemViewModel>()", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("_providerList.SelectedIndex(_selectedProviderIndex.Bind.Value);", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("BuildProviderListItem(value)", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("BuildProviderListItemMarkup(value.GetValue())", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("BuildProviderListItem(value.GetValue())", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("new Select<ModelProviderEditorItemViewModel>()", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("ModelProviderEditorItemViewModel.IBindings", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("CreateBinding(", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("CreateTrackedCheckBox(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("LoadDefinitionsIntoDialog(", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("private sealed class ModelProviderEditorItem", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("ScheduleProviderSelectionSync", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("_detailVersion", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("_diagnosticsVersion", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("item.Changed +=", StringComparison.Ordinal));
        Assert.IsTrue(viewModelSource.Contains("internal sealed partial class ModelProviderEditorItemViewModel", StringComparison.Ordinal));
        Assert.IsTrue(viewModelSource.Contains("[Bindable]", StringComparison.Ordinal));
        Assert.IsTrue(viewModelSource.Contains("public partial ModelProviderLastTestState LastTestState { get; private set; }", StringComparison.Ordinal));
        Assert.IsFalse(viewModelSource.Contains("public event Action<ModelProviderEditorItemViewModel, bool>? Changed;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_GuardsAgainstClosingDuringActiveProviderOperations()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("private int _activeOperationCount;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("Please wait for the current provider operation to complete before closing this dialog.", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("TryBeginDialogOperation(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("EndDialogOperation();", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProviderFrontendCoordinator_TestProvider_DisposesBackendWithoutDoubleStop()
    {
        var coordinatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ProviderFrontendCoordinator.cs"));

        Assert.IsTrue(coordinatorSource.Contains("await using var _ = backend;", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("await backend.StartAsync(cancellationToken);", StringComparison.Ordinal));
        Assert.IsTrue(coordinatorSource.Contains("var models = await backend.ListModelsAsync(cancellationToken);", StringComparison.Ordinal));
        Assert.IsFalse(coordinatorSource.Contains("await backend.StopAsync(cancellationToken);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_CachesValidationMessagesToAvoidComputedLoops()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("ValidationMessage? cachedMessage = null;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("cachedText = text;", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("return cachedMessage;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_QueuesProviderOperationsOutsideInputTracking()
    {
        var dialogSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ModelProvidersDialog.cs"));

        Assert.IsTrue(dialogSource.Contains("_dialog.Dispatcher.InvokeAsync(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains("_ = Task.Run(", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Click(() => StartReload(confirmWhenDirty: true));", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Click(StartSave);", StringComparison.Ordinal));
        Assert.IsTrue(dialogSource.Contains(".Click(() => StartTest(item));", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("PostToUi(", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("app.Post(action);", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("RunAfterTracking", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains(".Click(() => _ = TestSelectedAsync())", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("RestoreTerminalInputState", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("StartInput(new TerminalInputOptions", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("UseRawMode()", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("SetInputEcho(enabled: false)", StringComparison.Ordinal));
        Assert.IsFalse(dialogSource.Contains("EnableMouse(TerminalMouseMode.Drag)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SidebarStateIndicators_KeepTreeIconsAndRefreshOnDraftOrRunChanges()
    {
        var compositionSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendComposition.cs"));
        var workspaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ShellWorkspaceCoordinator.cs"));
        var sidebarViewSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarView.cs"));

        Assert.IsTrue(compositionSource.Contains("new PromptDraftUiCoordinator(", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("_workspaceContext.DispatchToUi(", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("_workspaceContext.RefreshSidebarProjection();", StringComparison.Ordinal));
        Assert.IsFalse(sidebarViewSource.Contains("new Rune(' ')", StringComparison.Ordinal));
        Assert.IsTrue(sidebarViewSource.Contains("Icon = projection.Icon,", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PopupPresenters_RestorePromptFocusWhenClosed()
    {
        var controlsSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Controls", "AnchoredPopupView.cs"));
        var usageSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Usage", "SessionUsagePresenter.cs"));
        var threadInfoSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Presentation", "Threads", "ThreadInfoPresenter.cs"));

        Assert.IsTrue(controlsSource.Contains("_onClosed?.Invoke();", StringComparison.Ordinal));
        Assert.IsTrue(usageSource.Contains("new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent), _focusPromptEditor)", StringComparison.Ordinal));
        Assert.IsTrue(threadInfoSource.Contains("new AnchoredPopupView(() => _createComputedVisual(BuildPopupContent), _focusPromptEditor)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaSource_UsesBindingsInsteadOfRegisterDynamicUpdate()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        var workspaceSource = File.ReadAllText(Path.Combine(codeAltaRoot, "Views", "ThreadWorkspaceView.cs"));

        AssertSourceDoesNotContain(sourceFiles, "RegisterDynamicUpdate(");
        AssertSourceDoesNotContain(sourceFiles, "BindingManager.");
        AssertSourceDoesNotContain(sourceFiles, "BindableObserver<");
        Assert.IsTrue(workspaceSource.Contains("QueuedPromptListView.Build(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_SourceStaysWithinFacadeSizeBudget()
    {
        var appPath = Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs");
        var appSize = new FileInfo(appPath).Length;

        Assert.IsTrue(appSize < 37500, $"CodeAltaApp.cs exceeded the facade size budget: {appSize} bytes.");
    }

    [TestMethod]
    public void HelpAndWorkspaceCommands_ShareShellCommandCatalog()
    {
        var helpSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Help", "ShellHelpContentBuilder.cs"));
        var surfaceSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Frontend", "Commands", "ShellCommandSurfaceCoordinator.cs"));

        Assert.IsTrue(helpSource.Contains("ShellCommandCatalog.Commands", StringComparison.Ordinal));
        Assert.IsTrue(surfaceSource.Contains("ShellCommandCatalog.Get(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShellSelectionState_UsesExplicitSelectionModel()
    {
        var selectionStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Models", "ShellSelectionState.cs"));

        Assert.IsTrue(selectionStateSource.Contains("ShellSelection Selection", StringComparison.Ordinal));
        Assert.IsFalse(selectionStateSource.Contains("bool DraftTabOpen", StringComparison.Ordinal));
        Assert.IsFalse(selectionStateSource.Contains("bool GlobalScopeSelected", StringComparison.Ordinal));
        Assert.IsFalse(selectionStateSource.Contains("string? SelectedThreadId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ThreadSelectionContext_ExposesSelectionModelInsteadOfLegacySelectionBooleans()
    {
        var contextSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "Context", "ThreadSelectionContext.cs"));

        Assert.IsTrue(contextSource.Contains("public ShellSelection Selection", StringComparison.Ordinal));
        Assert.IsTrue(contextSource.Contains("public WorkspaceTarget Target", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public bool DraftTabOpen", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public bool GlobalScopeSelected", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public string? SelectedProjectId", StringComparison.Ordinal));
        Assert.IsFalse(contextSource.Contains("public string? SelectedThreadId", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaFrontendCallbacks_AvoidsLeakingShellAndSelectionLookupBuckets()
    {
        var callbacksSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "CodeAltaFrontendCallbacks.cs"));

        Assert.IsFalse(callbacksSource.Contains("CodeAltaApp App", StringComparison.Ordinal));
        Assert.IsFalse(callbacksSource.Contains("Func<ShellSelection> GetSelection", StringComparison.Ordinal));
        Assert.IsFalse(callbacksSource.Contains("Func<AgentBackendId> GetPreferredBackendId", StringComparison.Ordinal));
        Assert.IsFalse(callbacksSource.Contains("Func<ProjectDescriptor?> GetSelectedProject", StringComparison.Ordinal));
        Assert.IsFalse(callbacksSource.Contains("GetPromptUnavailableStatus", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FleetConcepts_ReuseAgentIdentityAndAvoidFrontendAgentRegistry()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        var workspaceTargetSource = File.ReadAllText(Path.Combine(codeAltaRoot, "Models", "WorkspaceTarget.cs"));

        Assert.IsTrue(workspaceTargetSource.Contains("AgentIdentity", StringComparison.Ordinal));
        Assert.IsTrue(workspaceTargetSource.Contains("AgentScope", StringComparison.Ordinal));
        AssertSourceDoesNotContain(sourceFiles, "class AgentRegistry");
        AssertSourceDoesNotContain(sourceFiles, "interface IAgentRegistry");
    }

    [TestMethod]
    public void OpenThreadState_SplitsSessionWorkspaceAndTimelineLayers()
    {
        var openThreadStateSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "State", "OpenThreadState.cs"));

        Assert.IsTrue(openThreadStateSource.Contains("ThreadWorkspaceState Workspace", StringComparison.Ordinal));
        Assert.IsTrue(openThreadStateSource.Contains("ThreadTimelineState TimelineState", StringComparison.Ordinal));
        Assert.IsTrue(openThreadStateSource.Contains("ThreadSessionState Session", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AppContexts_DoNotExposeConcreteSelectorEditorOrLayoutControls()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var chatSelectorContextSource = File.ReadAllText(Path.Combine(codeAltaRoot, "App", "Context", "ChatSelectorStateContext.cs"));
        var workspaceContextSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "Context", "ShellWorkspaceContext.cs"));

        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "App", "Context", "ChatSelectorUiContext.cs")));
        Assert.IsFalse(chatSelectorContextSource.Contains("public Select<", StringComparison.Ordinal));
        Assert.IsFalse(chatSelectorContextSource.Contains("GetThreadInput()", StringComparison.Ordinal));
        Assert.IsFalse(workspaceContextSource.Contains("GetThreadPaneLayout()", StringComparison.Ordinal));
        Assert.IsFalse(workspaceContextSource.Contains("GetThreadBodySplitter()", StringComparison.Ordinal));
        Assert.IsFalse(workspaceContextSource.Contains("GetThreadInput()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_IsNoLongerPartial()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var partialFiles = Directory
            .EnumerateFiles(codeAltaRoot, "CodeAltaApp*.cs", SearchOption.AllDirectories)
            .Where(static file => File.ReadAllText(file).Contains("partial class CodeAltaApp", StringComparison.Ordinal))
            .Select(static file => Path.GetRelativePath(GetCodeAltaSourceRoot(), file).Replace('\\', '/'))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            string.Empty,
            string.Join("|", partialFiles));
    }

    private static void AssertSourceDoesNotContain(IEnumerable<string> sourceFiles, string pattern)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var matches = sourceFiles
            .Where(file => File.ReadAllText(file).Contains(pattern, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), matches, $"Found unexpected pattern '{pattern}'.");
    }

    private static string GetCodeAltaSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "CodeAlta");
            if (Directory.Exists(Path.Combine(candidate, "App")) &&
                Directory.Exists(Path.Combine(candidate, "Views")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate the CodeAlta source directory from the test output path.");
        return null!;
    }
}
