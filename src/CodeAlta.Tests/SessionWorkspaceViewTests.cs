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
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionWorkspaceViewTests
{
    [TestMethod]
    public void ActivateSessionTabContent_KeepsPromptPanelsWithStableTabContents()
    {
        var view = CreateSessionWorkspaceView();
        var firstContent = (VSplitter)view.CreateSessionTabContent("session-1", new TextBlock("First"));
        var secondContent = (VSplitter)view.CreateSessionTabContent("session-2", new TextBlock("Second"));

        view.ActivateSessionTabContent("session-1");

        var firstPromptPanel = firstContent.Second;
        Assert.IsNotNull(firstPromptPanel);
        Assert.AreSame(firstPromptPanel, view.SessionBottomPanel);
        Assert.AreSame(firstContent, firstPromptPanel.Parent);

        view.ActivateSessionTabContent("session-2");

        Assert.AreSame(firstPromptPanel, firstContent.Second);
        Assert.IsNotNull(secondContent.Second);
        Assert.AreNotSame(firstPromptPanel, secondContent.Second);
        Assert.AreSame(view.SessionBottomPanel, secondContent.Second);
        Assert.AreSame(secondContent, secondContent.Second.Parent);
    }

    [TestMethod]
    public void CreateSessionTabContent_RecreatesCleanContentAfterRemoval()
    {
        var view = CreateSessionWorkspaceView();
        var primary = new TextBlock("Session");
        var firstContent = view.CreateSessionTabContent("session-1", primary);

        view.RemoveTabPage("session-1");
        var recreatedContent = view.CreateSessionTabContent("session-1", primary);

        Assert.AreNotSame(firstContent, recreatedContent);
        Assert.AreSame(recreatedContent, primary.Parent);
    }

    [TestMethod]
    public void CreateSessionTabContent_IsIdempotentForRememberedTabContent()
    {
        var view = CreateSessionWorkspaceView();
        var primary = new TextBlock("Session");
        var firstContent = view.CreateSessionTabContent("session-1", primary);

        var repeatedContent = view.CreateSessionTabContent("session-1", primary);

        Assert.AreSame(firstContent, repeatedContent);
        Assert.AreSame(firstContent, primary.Parent);
    }

    [TestMethod]
    public void RemoveTabPage_DetachesOwnedPromptContent()
    {
        var view = CreateSessionWorkspaceView();
        var content = (VSplitter)view.CreateSessionTabContent("session-1", new TextBlock("Session"));
        var primary = content.First;
        var promptPanel = content.Second;

        view.RememberTabPage("session-1", new TabPage(new TextBlock("Session header"), content));
        view.RemoveTabPage("session-1");

        Assert.IsNull(content.First);
        Assert.IsNull(content.Second);
        Assert.IsNull(primary?.Parent);
        Assert.IsNull(promptPanel?.Parent);
        Assert.IsFalse(view.TryGetPromptPanel("session-1", out _));
    }

    [TestMethod]
    public void AskMode_ExpandsQuestionsAndRestoresPreviousSplitterRatio()
    {
        var view = CreateSessionWorkspaceView();
        var primary = new TextBlock("Timeline");
        var fileReview = new TextBlock("File review");
        var askForm = new TextBlock("Questions");
        var content = (VSplitter)view.CreateSessionTabContent("session-1", primary);
        var promptPanel = content.Second;
        content.Ratio = 0.82;

        Assert.IsTrue(view.EnterAskMode("session-1", askForm, fileReview));

        Assert.AreEqual(0.60, content.Ratio);
        Assert.AreSame(fileReview, content.First);
        Assert.AreSame(askForm, content.Second);

        Assert.IsTrue(view.ExitAskMode("session-1"));

        Assert.AreEqual(0.82, content.Ratio);
        Assert.AreSame(primary, content.First);
        Assert.AreSame(promptPanel, content.Second);
    }

    [TestMethod]
    public void StatusBar_TextRegionsUseDynamicTextBindings()
    {
        var view = CreateSessionWorkspaceView();

        var bottomPanel = Assert.IsInstanceOfType<DockLayout>(view.SessionBottomPanel);
        var topStack = Assert.IsInstanceOfType<VStack>(bottomPanel.Top);
        var statusLine = Assert.IsInstanceOfType<StatusBar>(topStack.Children[2]);
        var leftStatus = Assert.IsInstanceOfType<HStack>(statusLine.LeftText);
        var statusPrefix = Assert.IsInstanceOfType<Center>(leftStatus.Children[0]);
        var statusPrefixContent = Assert.IsInstanceOfType<HStack>(statusPrefix.Content);

        Assert.IsInstanceOfType<Spinner>(statusPrefixContent.Children[0]);
        Assert.IsInstanceOfType<Markup>(statusPrefixContent.Children[1]);
        Assert.IsInstanceOfType<TextBlock>(leftStatus.Children[1]);
        Assert.IsInstanceOfType<TextBlock>(statusLine.RightText);
    }

    [TestMethod]
    public void SyncActivePromptPanelProjection_UpdatesOnlyActivePromptPanelChrome()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateSessionWorkspaceView(shellViewModel, workspaceViewModel, promptComposerViewModel);
        _ = view.CreateSessionTabContent("session-1", new TextBlock("First"));
        _ = view.CreateSessionTabContent("session-2", new TextBlock("Second"));

        shellViewModel.StatusText = "first status";
        workspaceViewModel.ModelProviderOptions = [new ModelProviderOption(new("codex"), "Codex")];
        workspaceViewModel.SetPromptStripItems([CreatePromptStripItem("first-queued")], hasQueuedPrompts: true);
        promptComposerViewModel.PromptImageAttachmentVersion = 1;
        view.ActivateSessionTabContent("session-1");
        view.SyncActivePromptPanelProjection();
        var firstPanel = GetPromptPanel(view, "session-1");
        firstPanel.PromptComposerViewModel.AlwaysEnqueue = true;

        shellViewModel.StatusText = "second status";
        workspaceViewModel.ModelProviderOptions = [new ModelProviderOption(new("copilot"), "Copilot")];
        workspaceViewModel.SetPromptStripItems([CreatePromptStripItem("second-queued")], hasQueuedPrompts: true);
        promptComposerViewModel.PromptImageAttachmentVersion = 5;
        view.ActivateSessionTabContent("session-2");
        view.SyncActivePromptPanelProjection();
        view.RefreshActivePromptImages();
        var secondPanel = GetPromptPanel(view, "session-2");

        Assert.AreEqual("first status", firstPanel.ShellViewModel.StatusText);
        Assert.AreEqual("Codex", firstPanel.WorkspaceViewModel.ModelProviderOptions[0].Label);
        Assert.AreEqual("first-queued", firstPanel.WorkspaceViewModel.PromptStripItems[0].Id);
        Assert.AreEqual(1, firstPanel.PromptComposerViewModel.PromptImageAttachmentVersion);
        Assert.IsTrue(firstPanel.PromptComposerViewModel.AlwaysEnqueue);
        Assert.AreEqual("second status", secondPanel.ShellViewModel.StatusText);
        Assert.AreEqual("Copilot", secondPanel.WorkspaceViewModel.ModelProviderOptions[0].Label);
        Assert.AreEqual("second-queued", secondPanel.WorkspaceViewModel.PromptStripItems[0].Id);
        Assert.AreEqual(6, secondPanel.PromptComposerViewModel.PromptImageAttachmentVersion);
        Assert.IsFalse(secondPanel.PromptComposerViewModel.AlwaysEnqueue);
    }

    [TestMethod]
    public void SyncModelProviderSelectorItems_DoesNotTouchRemovedPromptPanelSelectors()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel
        {
            ModelProviderOptions = [new ModelProviderOption(new("codex"), "Codex")],
        };
        var view = CreateSessionWorkspaceView(workspaceViewModel: workspaceViewModel);
        _ = view.CreateSessionTabContent("session-1", new TextBlock("Session"));
        view.ActivateSessionTabContent("session-1");
        view.SyncModelProviderSelectorItems(workspaceViewModel);
        var removedPanel = GetPromptPanel(view, "session-1");

        view.RemoveTabPage("session-1");
        workspaceViewModel.ModelProviderOptions = [new ModelProviderOption(new("copilot"), "Copilot")];
        view.SyncModelProviderSelectorItems(workspaceViewModel);

        Assert.AreEqual(1, removedPanel.ModelProviderSelectorView.ModelProviderSelect.Items.Count);
        Assert.AreEqual("Codex", removedPanel.ModelProviderSelectorView.ModelProviderSelect.Items[0].Label);
    }

    [TestMethod]
    public void SyncModelProviderSelectorItems_ReplacesSelectItems()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new SessionWorkspaceViewModel
        {
            ModelProviderOptions = [new ModelProviderOption(new("codex"), "Codex")],
            ModelOptions = [new ChatModelOption("gpt-5", "GPT-5")],
            ReasoningOptions = [new ChatReasoningOption(Agent.AgentReasoningEffort.High, "High")],
        };
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateSessionWorkspaceView(shellViewModel, workspaceViewModel, promptComposerViewModel);

        view.SyncModelProviderSelectorItems(workspaceViewModel);

        var providerSelect = GetPrivateField<Select<ModelProviderOption>>(view, "ModelProviderSelect");
        var modelSelect = GetPrivateField<Select<ChatModelOption>>(view, "ChatModelSelect");
        var reasoningSelect = GetPrivateField<Select<ChatReasoningOption>>(view, "ChatReasoningSelect");

        Assert.AreEqual(1, providerSelect.Items.Count);
        Assert.AreEqual("Codex", providerSelect.Items[0].Label);
        Assert.AreEqual(1, modelSelect.Items.Count);
        Assert.AreEqual("GPT-5", modelSelect.Items[0].Label);
        Assert.AreEqual(1, reasoningSelect.Items.Count);
        Assert.AreEqual("High", reasoningSelect.Items[0].Label);

        workspaceViewModel.ModelProviderOptions =
        [
            new ModelProviderOption(new("codex"), "Codex"),
            new ModelProviderOption(new("copilot"), "Copilot"),
        ];
        workspaceViewModel.ModelOptions = [new ChatModelOption("gpt-5.1", "GPT-5.1")];
        workspaceViewModel.ReasoningOptions = [new ChatReasoningOption(Agent.AgentReasoningEffort.Low, "Low")];

        view.SyncModelProviderSelectorItems(workspaceViewModel);

        Assert.AreEqual(2, providerSelect.Items.Count);
        Assert.AreEqual("Copilot", providerSelect.Items[1].Label);
        Assert.AreEqual("GPT-5.1", modelSelect.Items[0].Label);
        Assert.AreEqual("Low", reasoningSelect.Items[0].Label);
    }

    [TestMethod]
    public void SyncModelProviderSelectorItems_AppliesSelectedIndexesToSelectControls()
    {
        var workspaceViewModel = new SessionWorkspaceViewModel
        {
            ModelProviderOptions =
            [
                new ModelProviderOption(new("codex"), "Codex"),
                new ModelProviderOption(new("copilot"), "Copilot"),
            ],
            SelectedModelProviderIndex = 1,
            ModelOptions =
            [
                new ChatModelOption("gpt-5", "GPT-5"),
                new ChatModelOption("gpt-5.1", "GPT-5.1"),
            ],
            SelectedModelIndex = 1,
            ReasoningOptions =
            [
                new ChatReasoningOption(Agent.AgentReasoningEffort.Low, "Low"),
                new ChatReasoningOption(Agent.AgentReasoningEffort.High, "High"),
            ],
            SelectedReasoningIndex = 1,
        };
        var providerChangeCount = 0;
        var modelChangeCount = 0;
        var reasoningChangeCount = 0;
        var view = CreateSessionWorkspaceView(
            workspaceViewModel: workspaceViewModel,
            modelProviderSelectorController: ModelProviderSelectorController.Create(
                _ => providerChangeCount++,
                _ => modelChangeCount++,
                _ => reasoningChangeCount++,
                static () => { }));

        view.SyncModelProviderSelectorItems(workspaceViewModel);

        var providerSelect = GetPrivateField<Select<ModelProviderOption>>(view, "ModelProviderSelect");
        var modelSelect = GetPrivateField<Select<ChatModelOption>>(view, "ChatModelSelect");
        var reasoningSelect = GetPrivateField<Select<ChatReasoningOption>>(view, "ChatReasoningSelect");
        Assert.AreEqual(1, GetSelectSelectedIndexField(providerSelect));
        Assert.AreEqual(1, GetSelectSelectedIndexField(modelSelect));
        Assert.AreEqual(1, GetSelectSelectedIndexField(reasoningSelect));
        Assert.AreEqual(0, providerChangeCount);
        Assert.AreEqual(0, modelChangeCount);
        Assert.AreEqual(0, reasoningChangeCount);
    }

    [TestMethod]
    public void ToggleControls_UseCheckBoxesBoundToViewModels()
    {
        var shellViewModel = new CodeAltaShellViewModel();
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel
        {
            AlwaysEnqueue = true,
            CanAlwaysEnqueue = true,
        };
        var view = CreateSessionWorkspaceView(shellViewModel, workspaceViewModel, promptComposerViewModel);

        Assert.IsTrue(view.AlwaysEnqueueCheckBox.IsChecked);
        Assert.IsTrue(view.AlwaysEnqueueCheckBox.IsEnabled);
    }

    [TestMethod]
    public void BottomBarPromptAndModelButtons_OpenDialogs()
    {
        var promptOpenCount = 0;
        var modelOpenCount = 0;
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var agentPromptSelectorView = new AgentPromptSelectorView(
            workspaceViewModel,
            AgentPromptSelectorController.Create(static _ => { }, () => promptOpenCount++));
        var modelProviderSelectorView = new ModelProviderSelectorView(
            workspaceViewModel,
            promptComposerViewModel,
            ModelProviderSelectorController.Create(
                static _ => { },
                static _ => { },
                static _ => { },
                static () => { },
                () => modelOpenCount++));
        var promptButton = agentPromptSelectorView.PromptDialogButton;
        var modelButton = modelProviderSelectorView.ModelsDialogButton;

        Assert.AreEqual("Agent->", GetButtonText(promptButton));
        Assert.AreEqual("Model->", GetButtonText(modelButton));
        Assert.AreEqual(ControlTone.Default, promptButton.Tone);
        Assert.AreEqual(ControlTone.Default, modelButton.Tone);

        var root = new HStack(agentPromptSelectorView.Root, modelProviderSelectorView.Root);
        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            root,
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(promptButton);
            DispatchKeyEvent(app, TerminalKey.Enter);
            app.Focus(modelButton);
            DispatchKeyEvent(app, TerminalKey.Enter);

            Assert.AreEqual(1, promptOpenCount);
            Assert.AreEqual(1, modelOpenCount);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void FocusModelProviderSelector_FocusesProviderSelect()
    {
        var view = CreateSessionWorkspaceView();
        var providerSelect = GetPrivateField<Select<ModelProviderOption>>(view, "ModelProviderSelect");

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

            Assert.AreSame(providerSelect, app.FocusedElement);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void CommandBar_DefaultsToSingleLine()
    {
        var view = CreateSessionWorkspaceView();

        Assert.IsFalse(view.SessionCommandBar.MultiLine);
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
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateSessionWorkspaceView(
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
        var workspaceViewModel = new SessionWorkspaceViewModel();
        var promptComposerViewModel = new PromptComposerViewModel();
        var view = CreateSessionWorkspaceView(
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
        var view = CreateSessionWorkspaceView(promptText: promptText);

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

    private static SessionPromptPanel GetPromptPanel(SessionWorkspaceView view, string tabId)
    {
        Assert.IsTrue(view.TryGetPromptPanel(tabId, out var panel));
        return panel;
    }

    private static int GetSelectSelectedIndexField<T>(Select<T> select)
    {
        var field = typeof(Select<T>).GetField("_selectedIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (int)field.GetValue(select)!;
    }

    private static string GetButtonText(Button button)
        => Assert.IsInstanceOfType<TextBlock>(button.Content).Text!;

    private static void DispatchKeyEvent(TerminalApp app, TerminalKey key)
        => InvokeTerminalApp(app, "DispatchKeyEvent", new TerminalKeyEvent { Key = key }, true);

    private static PromptStripItem CreatePromptStripItem(string id)
        => new(PromptStripItemKind.QueuedPrompt, id, id, id, ImageCount: 0, RemainingCount: null);

    private static SessionWorkspaceView CreateSessionWorkspaceView(
        CodeAltaShellViewModel? shellViewModel = null,
        SessionWorkspaceViewModel? workspaceViewModel = null,
        PromptComposerViewModel? promptComposerViewModel = null,
        ShellCommandSurfaceCoordinator? shellCommandSurfaceCoordinator = null,
        SessionWorkspaceChromeController? chromeController = null,
        PromptComposerViewController? promptComposerController = null,
        QueuedPromptStripController? queuedPromptController = null,
        ModelProviderSelectorController? modelProviderSelectorController = null,
        SessionTabHostController? sessionTabHostController = null,
        IProjectFileSearchService? projectFileSearchService = null,
        Func<string?>? getPromptReferenceProjectRoot = null,
        State<string?>? promptText = null)
        => new(
            shellViewModel ?? new CodeAltaShellViewModel(),
            workspaceViewModel ?? new SessionWorkspaceViewModel(),
            promptComposerViewModel ?? new PromptComposerViewModel(),
            shellCommandSurfaceCoordinator ?? TestShellCommandSurface.Create(),
            chromeController ?? SessionWorkspaceChromeController.Empty,
            promptComposerController ?? PromptComposerViewController.Create(static _ => { }, static () => { }, static () => { }, static () => { }, static () => { }),
            queuedPromptController ?? QueuedPromptStripController.Create(
                static _ => { },
                static _ => { },
                static _ => { },
                static _ => { },
                static (_, _) => { },
                static (_, _) => { },
                static (onAccepted, placeholder) => SessionWorkspaceView.CreateStyledPromptEditor(onAccepted, null, null, placeholder)),
            AgentPromptSelectorController.Create(static _ => { }),
            modelProviderSelectorController ?? ModelProviderSelectorController.Create(static _ => { }, static _ => { }, static _ => { }, static () => { }),
            sessionTabHostController ?? SessionTabHostController.Create(static _ => { }),
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

    private static ChatPromptEditor GetExpandedPromptEditor(SessionWorkspaceView view)
    {
        var dialog = GetExpandedPromptDialog(view);
        var scrollViewer = Assert.IsInstanceOfType<ScrollViewer>(dialog.Content);
        return Assert.IsInstanceOfType<ChatPromptEditor>(scrollViewer.Content);
    }

    private static Dialog GetExpandedPromptDialog(SessionWorkspaceView view)
        => GetPrivateField<Dialog>(GetPromptComposerView(view), "_expandedPromptDialog");

    private static object GetPromptComposerView(SessionWorkspaceView view)
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
