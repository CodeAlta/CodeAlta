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
        var sidebarSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SidebarCoordinator.cs"));
        var appSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "CodeAltaApp.cs"));

        Assert.IsTrue(sidebarSource.Contains("verifyBindableAccess();", StringComparison.Ordinal));
        Assert.IsTrue(appSource.Contains("private T ReadBindableState<T>(Func<T> read)", StringComparison.Ordinal));
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
