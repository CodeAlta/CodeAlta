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
    public void RuntimeLayer_UsesBindableReadHelperForViewModelReads()
    {
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(appSource.Contains("ReadBindableState(() => _sidebarViewModel.DraftThreadTitle?.Trim())", StringComparison.Ordinal));
        Assert.IsFalse(appSource.Contains("var title = _sidebarViewModel.DraftThreadTitle?.Trim()", StringComparison.Ordinal));
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
        Assert.IsTrue(File.Exists(Path.Combine(codeAltaRoot, "Presentation", "Controls", "SessionUsagePopupView.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(codeAltaRoot, "Views", "SessionUsagePopupView.cs")));
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

        Assert.IsTrue(appSource.Contains("_threadRuntimeEventCoordinator.ApplyRuntimeEvent", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadRuntimeEventCoordinator.TryRenderInteraction", StringComparison.Ordinal));
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
        var creationSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "ThreadCreationCoordinator.cs"));

        Assert.IsTrue(appSource.Contains("_threadCommandCoordinator.SendSelectedThreadPromptAsync", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadCommandCoordinator.DelegateSelectedThreadAsync", StringComparison.Ordinal));
        Assert.IsTrue(creationSource.Contains("_buildPreferredExecutionOptions(", StringComparison.Ordinal));
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

        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.LoadInitialCatalogStateAsync", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("TryResolveInitialCatalogState(cancellationToken)", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.ApplyRecoveredCatalogState", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("_threadStateCoordinator.EnsureThreadTab", StringComparison.Ordinal));
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
    public void CodeAltaSource_UsesBindingsInsteadOfRegisterDynamicUpdate()
    {
        var codeAltaRoot = GetCodeAltaSourceRoot();
        var sourceFiles = Directory.EnumerateFiles(codeAltaRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        var workspaceSource = File.ReadAllText(Path.Combine(codeAltaRoot, "Views", "ThreadWorkspaceView.cs"));

        AssertSourceDoesNotContain(sourceFiles, "RegisterDynamicUpdate(");
        AssertSourceDoesNotContain(sourceFiles, "BindingManager.");
        Assert.IsTrue(workspaceSource.Contains(".SelectedIndex(workspaceViewModel.Bind.SelectedTabIndex)", StringComparison.Ordinal));
        Assert.IsTrue(workspaceSource.Contains("QueuedPromptListView.Build(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodeAltaApp_SourceStaysWithinFacadeSizeBudget()
    {
        var appPath = Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs");
        var appSize = new FileInfo(appPath).Length;

        Assert.IsTrue(appSize < 35000, $"CodeAltaApp.cs exceeded the facade size budget: {appSize} bytes.");
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
