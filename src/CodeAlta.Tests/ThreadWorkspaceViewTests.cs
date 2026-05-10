using System.Diagnostics;
using System.Reflection;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Catalog;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadWorkspaceViewTests
{
    [TestMethod]
    public void ActivateThreadTabContent_KeepsPromptPanelsWithStableTabContents()
    {
        var view = CreateThreadWorkspaceView();
        var firstContent = (VSplitter)view.CreateThreadTabContent("thread-1", new TextBlock("First"));
        var secondContent = (VSplitter)view.CreateThreadTabContent("thread-2", new TextBlock("Second"));

        view.ActivateThreadTabContent("thread-1");

        var firstPromptPanel = firstContent.Second;
        Assert.IsNotNull(firstPromptPanel);
        Assert.AreSame(firstPromptPanel, view.ThreadBottomPanel);
        Assert.AreSame(firstContent, firstPromptPanel.Parent);

        view.ActivateThreadTabContent("thread-2");

        Assert.AreSame(firstPromptPanel, firstContent.Second);
        Assert.IsNotNull(secondContent.Second);
        Assert.AreNotSame(firstPromptPanel, secondContent.Second);
        Assert.AreSame(view.ThreadBottomPanel, secondContent.Second);
        Assert.AreSame(secondContent, secondContent.Second.Parent);
    }

    [TestMethod]
    public void CreateThreadTabContent_ReusesExistingSplitterForAttachedPrimaryContent()
    {
        var view = CreateThreadWorkspaceView();
        var primary = new TextBlock("Thread");
        var firstContent = view.CreateThreadTabContent("thread-1", primary);

        view.RemoveTabPage("thread-1");
        var recoveredContent = view.CreateThreadTabContent("thread-1", primary);

        Assert.AreSame(firstContent, recoveredContent);
        Assert.AreSame(firstContent, primary.Parent);
    }

    [TestMethod]
    public void CreateThreadTabContent_IsIdempotentForRememberedTabContent()
    {
        var view = CreateThreadWorkspaceView();
        var primary = new TextBlock("Thread");
        var firstContent = view.CreateThreadTabContent("thread-1", primary);

        var repeatedContent = view.CreateThreadTabContent("thread-1", primary);

        Assert.AreSame(firstContent, repeatedContent);
        Assert.AreSame(firstContent, primary.Parent);
    }

    [TestMethod]
    public void ActivateThreadTabContent_RecoversRememberedSplitterAfterContentCacheWasCleared()
    {
        var view = CreateThreadWorkspaceView();
        var content = (VSplitter)view.CreateThreadTabContent("thread-1", new TextBlock("Thread"));

        var promptPanel = content.Second;

        view.RemoveTabPage("thread-1");
        content = (VSplitter)view.CreateThreadTabContent("thread-1", new TextBlock("Thread"));
        view.RememberTabPage("thread-1", new TabPage(new TextBlock("Thread header"), content));
        view.ActivateThreadTabContent("thread-1");

        Assert.AreSame(view.ThreadBottomPanel, content.Second);
        Assert.AreNotSame(promptPanel, content.Second);
        Assert.AreSame(content, view.ThreadBottomPanel.Parent);
    }

    [TestMethod]
    public void StatusBar_TextRegionsUseDynamicTextBindings()
    {
        var view = CreateThreadWorkspaceView();

        var bottomPanel = Assert.IsInstanceOfType<DockLayout>(view.ThreadBottomPanel);
        var topStack = Assert.IsInstanceOfType<VStack>(bottomPanel.Top);
        var statusLine = Assert.IsInstanceOfType<StatusBar>(topStack.Children[2]);
        var leftStatus = Assert.IsInstanceOfType<HStack>(statusLine.LeftText);

        Assert.IsInstanceOfType<TextBlock>(leftStatus.Children[1]);
        Assert.IsInstanceOfType<TextBlock>(statusLine.RightText);
    }

    [TestMethod]
    public void SyncModelProviderSelectorItems_ReplacesSelectItems()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel
        {
            ModelProviderOptions = [new ChatBackendOption(new("codex"), "Codex")],
            ModelOptions = [new ChatModelOption("gpt-5", "GPT-5")],
            ReasoningOptions = [new ChatReasoningOption(Agent.AgentReasoningEffort.High, "High")],
        };
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateThreadWorkspaceView(shellViewModel, workspaceViewModel, promptComposerViewModel);

        view.SyncModelProviderSelectorItems(workspaceViewModel);

        var backendSelect = GetPrivateField<Select<ChatBackendOption>>(view, "ChatBackendSelect");
        var modelSelect = GetPrivateField<Select<ChatModelOption>>(view, "ChatModelSelect");
        var reasoningSelect = GetPrivateField<Select<ChatReasoningOption>>(view, "ChatReasoningSelect");

        Assert.AreEqual(1, backendSelect.Items.Count);
        Assert.AreEqual("Codex", backendSelect.Items[0].Label);
        Assert.AreEqual(1, modelSelect.Items.Count);
        Assert.AreEqual("GPT-5", modelSelect.Items[0].Label);
        Assert.AreEqual(1, reasoningSelect.Items.Count);
        Assert.AreEqual("High", reasoningSelect.Items[0].Label);

        workspaceViewModel.ModelProviderOptions =
        [
            new ChatBackendOption(new("codex"), "Codex"),
            new ChatBackendOption(new("copilot"), "Copilot"),
        ];
        workspaceViewModel.ModelOptions = [new ChatModelOption("gpt-5.1", "GPT-5.1")];
        workspaceViewModel.ReasoningOptions = [new ChatReasoningOption(Agent.AgentReasoningEffort.Low, "Low")];

        view.SyncModelProviderSelectorItems(workspaceViewModel);

        Assert.AreEqual(2, backendSelect.Items.Count);
        Assert.AreEqual("Copilot", backendSelect.Items[1].Label);
        Assert.AreEqual("GPT-5.1", modelSelect.Items[0].Label);
        Assert.AreEqual("Low", reasoningSelect.Items[0].Label);
    }

    [TestMethod]
    public void ThreadInput_UsesHumanLabelsAndSlashCommandSearchText()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var closeTabBinding = new ThreadWorkspaceCommandBinding(
            ShellCommandCatalog.Get("CodeAlta.Thread.CloseTab"),
            static () => { });
        var steerBinding = new ThreadWorkspaceCommandBinding(
            ShellCommandCatalog.Get("CodeAlta.Thread.Steer"),
            static () => { });
        var view = CreateThreadWorkspaceView(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            [closeTabBinding, steerBinding]);

        var closeTabCommand = Assert.IsInstanceOfType<Command>(
            view.ThreadInput.Commands.Single(command => string.Equals(command.Id, "CodeAlta.Thread.CloseTab", StringComparison.Ordinal)));
        var steerCommand = Assert.IsInstanceOfType<Command>(
            view.ThreadInput.Commands.Single(command => string.Equals(command.Id, "CodeAlta.Thread.Steer", StringComparison.Ordinal)));

        Assert.AreEqual("Close Tab", closeTabCommand.LabelMarkup);
        Assert.AreEqual("close_tab", closeTabCommand.Name);
        StringAssert.Contains(closeTabCommand.SearchText, "/close_tab");
        StringAssert.Contains(closeTabCommand.SearchText, "/close");
        Assert.AreEqual("Steer", steerCommand.LabelMarkup);
        Assert.AreEqual(CommandPresentation.CommandBar, steerCommand.Presentation);
        Assert.IsNotNull(steerCommand.SearchText);
        Assert.IsFalse(steerCommand.SearchText.Contains("/steer", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToggleControls_UseCheckBoxesBoundToViewModels()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel
        {
            AlwaysEnqueue = true,
            CanAlwaysEnqueue = true,
        };
        var view = CreateThreadWorkspaceView(shellViewModel, workspaceViewModel, promptComposerViewModel);

        Assert.IsTrue(view.AlwaysEnqueueCheckBox.IsChecked);
        Assert.IsTrue(view.AlwaysEnqueueCheckBox.IsEnabled);
    }

    [TestMethod]
    public void FocusModelProviderSelector_FocusesProviderSelect()
    {
        var view = CreateThreadWorkspaceView();
        var backendSelect = GetPrivateField<Select<ChatBackendOption>>(view, "ChatBackendSelect");

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view.Root,
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            view.FocusModelProviderSelector();

            Assert.AreSame(backendSelect, app.FocusedElement);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void CommandBar_DefaultsToSingleLine()
    {
        var view = CreateThreadWorkspaceView();

        Assert.IsFalse(view.ThreadCommandBar.MultiLine);
    }

    [TestMethod]
    public void CommandBarToggleCommand_UsesCtrlGCtrlBShortcut()
    {
        var metadata = ShellCommandCatalog.Get("CodeAlta.Shell.ToggleCommandBarMultiLine");

        Assert.AreEqual(ShellCommandCatalog.ToggleCommandBarMultiLineShortcutSequence, metadata.Sequence);
        Assert.IsTrue(metadata.ShowInCommandBar);
        Assert.IsTrue(metadata.ShowInCommandPalette);
    }

    [TestMethod]
    public void ExpandedPromptEditor_UsesProjectFileReferences()
    {
        using var tempDirectory = TempDirectory.Create();
        var projectRoot = tempDirectory.Path;
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), string.Empty);

        var searchSession = new FakeProjectFileSearchSession(
            CreateState(
                projectRoot,
                [CreateResult(projectRoot, "src/app.cs")],
                isRefreshing: false,
                candidateCount: 1));
        var searchService = new FakeProjectFileSearchService(searchSession);
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateThreadWorkspaceView(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            projectFileSearchService: searchService,
            getPromptReferenceProjectRoot: () => projectRoot);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view.Root,
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            view.OpenExpandedPromptDialog();

            var dialog = GetExpandedPromptDialog(view);
            var scrollViewer = Assert.IsInstanceOfType<ScrollViewer>(dialog.Content);
            var editor = Assert.IsInstanceOfType<ChatPromptEditor>(scrollViewer.Content);
            app.Focus(editor);

            InvokeTerminalApp(app, "DispatchTextInput", "@");

            Assert.IsTrue(WaitForCondition(app, () => editor.HasProjectFileReferencePopup, TimeSpan.FromSeconds(1)));
            Assert.IsTrue(WaitForCondition(app, () => editor.ProjectFileReferenceItems.Count == 1, TimeSpan.FromSeconds(1)));
            Assert.AreEqual("src/app.cs", editor.ProjectFileReferenceItems[0].Result.Item.RelativePath);
            Assert.AreEqual(1, searchSession.RefreshCount);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ExpandedPromptEditor_UsesHelpAndCommandPaletteShortcuts()
    {
        var helpCount = 0;
        var commandPaletteCount = 0;
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new ThreadWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateThreadWorkspaceView(
            shellViewModel,
            workspaceViewModel,
            promptComposerViewModel,
            promptComposerController: PromptComposerViewController.Create(
                static _ => { },
                static () => { },
                static () => { },
                () => helpCount++,
                () => commandPaletteCount++));

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view.Root,
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            view.OpenExpandedPromptDialog();

            var editor = GetExpandedPromptEditor(view);

            Assert.IsTrue(editor.TryHandleTransientShortcutInput("/"));
            Assert.IsTrue(editor.TryHandleTransientShortcutInput("?"));
            Assert.AreEqual(1, helpCount);
            Assert.AreEqual(1, commandPaletteCount);
            Assert.AreEqual(string.Empty, editor.Text);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ExpandedPromptEditor_CtrlEnterClosesDialogAndPreservesDraft()
    {
        var promptText = new State<string?>("draft prompt");
        var view = CreateThreadWorkspaceView(promptText: promptText);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            view.Root,
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            view.OpenExpandedPromptDialog();
            var editor = GetExpandedPromptEditor(view);
            app.Focus(editor);
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)terminalSession.Instance.Backend;
            backend.PushEvent(new TerminalKeyEvent
            {
                Key = TerminalKey.Enter,
                Modifiers = TerminalModifiers.Ctrl,
            });
            TickTerminalApp(app);

            Assert.IsNull(GetPrivateMemberValue(GetPromptComposerView(view), "_expandedPromptDialog"));
            Assert.AreEqual("draft prompt", promptText.Value);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
        where T : class
    {
        var property = instance.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            return Assert.IsInstanceOfType<T>(property.GetValue(instance));
        }

        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return Assert.IsInstanceOfType<T>(field.GetValue(instance));
    }

    private static ThreadWorkspaceView CreateThreadWorkspaceView(
        CodeAltaShellViewModel? shellViewModel = null,
        ThreadWorkspaceViewModel? workspaceViewModel = null,
        PromptComposerViewModel? promptComposerViewModel = null,
        IReadOnlyList<ThreadWorkspaceCommandBinding>? commandBindings = null,
        ThreadWorkspaceChromeController? chromeController = null,
        PromptComposerViewController? promptComposerController = null,
        QueuedPromptStripController? queuedPromptController = null,
        ModelProviderSelectorController? modelProviderSelectorController = null,
        ThreadTabHostController? threadTabHostController = null,
        IProjectFileSearchService? projectFileSearchService = null,
        Func<string?>? getPromptReferenceProjectRoot = null,
        State<string?>? promptText = null)
        => new(
            shellViewModel ?? new CodeAltaShellViewModel(),
            workspaceViewModel ?? new ThreadWorkspaceViewModel(),
            promptComposerViewModel ?? new PromptComposerViewModel(),
            commandBindings ?? [],
            chromeController ?? ThreadWorkspaceChromeController.Empty,
            promptComposerController ?? PromptComposerViewController.Create(static _ => { }, static () => { }, static () => { }, static () => { }, static () => { }),
            queuedPromptController ?? QueuedPromptStripController.Create(
                static _ => { },
                static _ => { },
                static _ => { },
                static _ => { },
                static (_, _) => { },
                static (_, _) => { },
                static (onAccepted, placeholder) => ThreadWorkspaceView.CreateStyledPromptEditor(onAccepted, null, null, placeholder)),
            modelProviderSelectorController ?? ModelProviderSelectorController.Create(static _ => { }, static _ => { }, static _ => { }, static () => { }),
            threadTabHostController ?? ThreadTabHostController.Create(static _ => { }),
            projectFileSearchService ?? NullProjectFileSearchService.Instance,
            getPromptReferenceProjectRoot ?? (static () => null),
            (_, _) => new PromptComposerSessionBinding(promptText ?? new State<string?>(string.Empty)),
            new State<float>(0));

    private static object? GetPrivateMemberValue(object instance, string fieldName)
    {
        var property = instance.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            return property.GetValue(instance);
        }

        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return field.GetValue(instance);
    }

    private static ChatPromptEditor GetExpandedPromptEditor(ThreadWorkspaceView view)
    {
        var dialog = GetExpandedPromptDialog(view);
        var scrollViewer = Assert.IsInstanceOfType<ScrollViewer>(dialog.Content);
        return Assert.IsInstanceOfType<ChatPromptEditor>(scrollViewer.Content);
    }

    private static Dialog GetExpandedPromptDialog(ThreadWorkspaceView view)
        => GetPrivateField<Dialog>(GetPromptComposerView(view), "_expandedPromptDialog");

    private static object GetPromptComposerView(ThreadWorkspaceView view)
        => GetPrivateField<object>(view, "_promptComposerView");

    private static ProjectFileSearchState CreateState(
        string projectRoot,
        IReadOnlyList<ProjectFileSearchResult> results,
        bool isRefreshing,
        int candidateCount,
        string query = "")
    {
        return new ProjectFileSearchState
        {
            Query = query,
            Results = results,
            IsRefreshing = isRefreshing,
            HasSnapshot = true,
            RefreshGeneration = 1,
            SnapshotGeneration = 1,
            CandidateCount = candidateCount,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProjectFileSearchResult CreateResult(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind = ProjectFileSearchItemKind.File,
        double score = 100)
        => new(CreateItem(projectRoot, relativePath, kind), score, false);

    private static ProjectFileSearchItem CreateItem(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind = ProjectFileSearchItemKind.File)
    {
        var basename = Path.GetFileName(relativePath);
        var extension = kind == ProjectFileSearchItemKind.Directory ? string.Empty : Path.GetExtension(basename);
        return new ProjectFileSearchItem
        {
            Kind = kind,
            ProjectRoot = projectRoot,
            RelativePath = relativePath.Replace('\\', '/'),
            FullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Basename = basename,
            ParentPath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty,
            Extension = extension,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            SearchFields = new ProjectFileSearchFields(
                basename.ToLowerInvariant(),
                relativePath.ToLowerInvariant(),
                relativePath.Split('/').Select(static segment => segment.ToLowerInvariant()).ToArray(),
                extension.ToLowerInvariant()),
            Usage = null,
        };
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName, params object[] arguments)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, arguments);
    }

    private static bool WaitForCondition(TerminalApp app, Func<bool> condition, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            TickTerminalApp(app);
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        TickTerminalApp(app);
        return condition();
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var method = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, [null]);
    }

    private sealed class FakeProjectFileSearchService(IProjectFileSearchSession session) : IProjectFileSearchService
    {
        public ValueTask<IProjectFileSearchSession> CreateSessionAsync(
            ProjectFileSearchSessionOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = session.RefreshAsync(cancellationToken);
            return ValueTask.FromResult(session);
        }

        public ValueTask<ProjectFileResolution> ResolveAsync(
            ProjectFileResolveQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RecordUsageAsync(
            ProjectFileUsageEvent usageEvent,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask InvalidateAsync(
            string projectRoot,
            ProjectFileInvalidationReason reason,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class FakeProjectFileSearchSession(ProjectFileSearchState initialState) : IProjectFileSearchSession
    {
        private ProjectFileSearchState _current = initialState;

        public ProjectFileSearchState Current => _current;

        public int RefreshCount { get; private set; }

        public event EventHandler<ProjectFileSearchStateChangedEventArgs>? Updated;

        public ValueTask SetQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            _current = _current with { Query = query };
            Updated?.Invoke(this, new ProjectFileSearchStateChangedEventArgs(_current));
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
