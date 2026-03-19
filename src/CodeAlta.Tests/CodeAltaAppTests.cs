using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Figlet;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaAppTests
{
    [TestMethod]
    public void CreatePendingChatMessage_CreatesStreamingMarkdownSynchronously()
    {
        var pending = CodeAltaApp.CreatePendingChatMessage("hello, who are you?");

        Assert.IsInstanceOfType<MarkdownControl>(pending.StreamingMarkdown);
        Assert.AreEqual(string.Empty, pending.StreamingMarkdown.Markdown);
        Assert.AreEqual(Align.Start, pending.StreamingMarkdown.VerticalAlignment);
    }

    [TestMethod]
    public async Task CreatePendingChatMessage_CanBeCreatedFromWorkerThread()
    {
        var pending = await Task.Run(() => CodeAltaApp.CreatePendingChatMessage("hello"));

        Assert.IsInstanceOfType<MarkdownControl>(pending.StreamingMarkdown);
        Assert.AreEqual(string.Empty, pending.StreamingMarkdown.Markdown);
    }

    [TestMethod]
    public void CopyButton_CopiesLatestStreamingMarkdown()
    {
        var pending = CodeAltaApp.CreatePendingChatMessage("hello");
        var root = new DocumentFlow();
        root.Items.Add(pending.AssistantItem);

        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(80, 20)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            TickTerminalApp(app);

            pending.StreamingMarkdown.Markdown = "Updated assistant reply";
            TickTerminalApp(app);

            var group = Assert.IsInstanceOfType<Group>(pending.StreamingMarkdown.Parent);
            var copyButton = Assert.IsInstanceOfType<Button>(group.TopRightText);
            var backend = (InMemoryTerminalBackend)session.Instance.Backend;

            backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Left, X = copyButton.Bounds.X, Y = copyButton.Bounds.Y });
            backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Up, Button = TerminalMouseButton.Left, X = copyButton.Bounds.X, Y = copyButton.Bounds.Y });
            TickTerminalApp(app);

            Assert.AreEqual("Updated assistant reply", session.Instance.Clipboard.Text);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void BuildUserPromptTimelineItems_OmitsRuleForFirstPromptAndAddsItAfterward()
    {
        var pending = CodeAltaApp.CreatePendingChatMessage("hello");

        var firstItems = CodeAltaApp.BuildUserPromptTimelineItems(pending.UserItem, hasSeenUserPrompt: false);
        var laterItems = CodeAltaApp.BuildUserPromptTimelineItems(pending.UserItem, hasSeenUserPrompt: true);

        Assert.AreEqual(1, firstItems.Count);
        Assert.AreEqual(2, laterItems.Count);
        Assert.AreSame(pending.UserItem.Content, firstItems[0].Content);
        Assert.AreSame(pending.UserItem.Content, laterItems[1].Content);

        Assert.IsInstanceOfType<FlowDocument>(laterItems[0].Content);
        var document = (FlowDocument)laterItems[0].Content;
        Assert.AreEqual(1, document.BlockCount);
        Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var visual = ((VisualDocumentFlowBlock)document.GetBlock(0)).CreateVisual();
        Assert.IsInstanceOfType<Rule>(visual);
    }

    [TestMethod]
    public async Task BuildUserPromptTimelineItems_CanBeCalledFromWorkerThread()
    {
        var pending = CodeAltaApp.CreatePendingChatMessage("hello");

        var items = await Task.Run(() => CodeAltaApp.BuildUserPromptTimelineItems(pending.UserItem, hasSeenUserPrompt: true));

        Assert.AreEqual(2, items.Count);
        Assert.IsInstanceOfType<FlowDocument>(items[0].Content);
    }

    [TestMethod]
    public void FormatChatContentMarkdown_PrefixesReasoningContent()
    {
        var markdown = CodeAltaApp.FormatChatContentMarkdown(AgentContentKind.Reasoning, "Inspecting the project.");

        Assert.AreEqual("Inspecting the project.", markdown);
    }

    [TestMethod]
    public void ReasoningContent_PromotesHeadingIntoHeaderAndTrimsBody()
    {
        const string content = "**Planning tool utilization**\n\nI should inspect the repository structure first.";

        var markdown = CodeAltaApp.FormatChatContentMarkdown(AgentContentKind.Reasoning, content);
        var headerSecondary = CodeAltaApp.GetChatContentHeaderSecondary(AgentContentKind.Reasoning, content);

        Assert.AreEqual("I should inspect the repository structure first.", markdown);
        Assert.AreEqual("Planning tool utilization", headerSecondary);
    }

    [TestMethod]
    public void FormatChatPlanMarkdown_RendersExplanationAndStatuses()
    {
        var markdown = CodeAltaApp.FormatChatPlanMarkdown(
            new AgentPlanSnapshot(
                AgentPlanChangeKind.Updated,
                "Need to update the terminal timeline.",
                [
                    new AgentPlanStep("Map the new event types.", AgentPlanStepStatus.Completed),
                    new AgentPlanStep("Render activity rows.", AgentPlanStepStatus.InProgress),
                    new AgentPlanStep("Add regression tests.", AgentPlanStepStatus.Pending),
                ]));

        StringAssert.Contains(markdown, "Updated.");
        StringAssert.Contains(markdown, "Need to update the terminal timeline.");
        StringAssert.Contains(markdown, "[x] Map the new event types.");
        StringAssert.Contains(markdown, "[~] Render activity rows.");
        StringAssert.Contains(markdown, "[ ] Add regression tests.");
    }

    [TestMethod]
    public void FormatChatActivityMarkdown_UsesConciseToolCallPresentation()
    {
        var markdown = CodeAltaApp.FormatChatActivityMarkdown(
            new AgentActivityEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.McpToolCall,
                AgentActivityPhase.Started,
                "tool-1",
                null,
                "search_workspace",
                "Searching the workspace."));

        StringAssert.Contains(markdown, "Name");
        StringAssert.Contains(markdown, "search_workspace");
        StringAssert.Contains(markdown, "Detail");
        StringAssert.Contains(markdown, "Searching the workspace.");
    }

    [TestMethod]
    public void FormatChatActivityMarkdown_CompactsLargeToolOutputs()
    {
        var payload = string.Join('\n', Enumerable.Range(1, 12).Select(static index => $"line {index}"));
        var markdown = CodeAltaApp.FormatChatActivityMarkdown(
            new AgentActivityEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.ToolCall,
                AgentActivityPhase.Completed,
                "tool-1",
                null,
                "view",
                payload));

        StringAssert.Contains(markdown.ToLowerInvariant(), "output omitted");
        Assert.IsFalse(markdown.Contains("line 12", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatChatActivityMarkdown_PrefersNormalizedCommandName()
    {
        using var detailsJson = JsonDocument.Parse(
            """
            {
              "command": "git remote -v",
              "rawCommand": "C:\\Program Files\\PowerShell\\7\\pwsh.exe -Command git remote -v"
            }
            """);

        var markdown = CodeAltaApp.FormatChatActivityMarkdown(
            new AgentActivityEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentActivityKind.CommandExecution,
                AgentActivityPhase.Started,
                "tool-1",
                null,
                @"C:\Program Files\PowerShell\7\pwsh.exe -Command git remote -v",
                null,
                detailsJson.RootElement.Clone()));

        StringAssert.Contains(markdown, "git remote -v");
        Assert.IsFalse(markdown.Contains(@"""C:\Program", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToolCallDetailMarkdown_DoesNotRenderStatusDetail()
    {
        var entry = new ToolCallEntryState(
            "call-1",
            new Button(new TextBlock("tool")),
            new Markup("tool"))
        {
            ActivityKind = AgentActivityKind.ToolCall,
            Status = ToolCallDisplayStatus.Completed,
            DisplayName = "view",
            StatusMessage = "Repeated in output",
            FirstSeenAt = DateTimeOffset.Parse("2026-03-15T10:00:00+00:00"),
            LastUpdatedAt = DateTimeOffset.Parse("2026-03-15T10:00:02+00:00"),
        };
        entry.OutputBuffer.AppendLine("Repeated in output");

        var markdown = ToolCallSummaryFormatter.BuildDetailMarkdown(entry);

        Assert.IsNotNull(markdown);
        Assert.IsFalse(markdown.Contains("Status Detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatChatSessionUpdateMarkdown_ReturnsMessageOnly()
    {
        var markdown = CodeAltaApp.FormatChatSessionUpdateMarkdown(
            new AgentSessionUpdateEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.UsageUpdated,
                "13116/128000 tokens"));

        Assert.AreEqual("13116/128000 tokens", markdown);
        StringAssert.Contains(CodeAltaApp.GetSessionUpdateHeader(AgentSessionUpdateKind.UsageUpdated), "Usage Updated");
    }

    [TestMethod]
    public void BuildHeaderText_UsesDraftScopeWhenNoThreadIsSelected()
    {
        var project = new ProjectDescriptor
        {
            Id = "project-1",
            Slug = "codealta",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\code\CodeAlta",
            DefaultBranch = "main",
            MarkdownBody = "# CodeAlta",
        };

        var globalHeader = CodeAltaApp.BuildHeaderText(
            thread: null,
            selectedProject: null,
            globalRoot: @"C:\Users\alexa\.codealta",
            preferredBackendId: AgentBackendIds.Codex.Value,
            globalScopeSelected: true);

        var projectHeader = CodeAltaApp.BuildHeaderText(
            thread: null,
            selectedProject: project,
            globalRoot: @"C:\Users\alexa\.codealta",
            preferredBackendId: AgentBackendIds.Copilot.Value,
            globalScopeSelected: false);

        Assert.AreEqual("CodeAlta | codex | global draft", globalHeader);
        Assert.AreEqual("CodeAlta | copilot | codealta draft", projectHeader);
    }

    [TestMethod]
    public void BuildDraftPromptMessage_ReflectsSelectedScope()
    {
        Assert.AreEqual("Send the first prompt to start a global thread.", CodeAltaApp.BuildDraftPromptMessage(globalScopeSelected: true));
        Assert.AreEqual("Send the first prompt to start a thread for the selected project.", CodeAltaApp.BuildDraftPromptMessage(globalScopeSelected: false));
    }

    [TestMethod]
    public void BuildWelcomeSubtitle_ReflectsCurrentScope()
    {
        var project = new ProjectDescriptor
        {
            DisplayName = "CodeAlta",
        };

        Assert.AreEqual("Global workspace ready for a new thread.", CodeAltaApp.BuildWelcomeSubtitle(null, globalScopeSelected: true));
        Assert.AreEqual("Project draft selected. Choose a project or start typing below.", CodeAltaApp.BuildWelcomeSubtitle(null, globalScopeSelected: false));
        Assert.AreEqual("Next thread will start in CodeAlta.", CodeAltaApp.BuildWelcomeSubtitle(project, globalScopeSelected: false));
    }

    [TestMethod]
    public void BuildWelcomeGuidanceLines_ReflectCurrentScope()
    {
        var project = new ProjectDescriptor
        {
            DisplayName = "CodeAlta",
        };

        CollectionAssert.AreEqual(
            new[]
            {
                "Use the prompt below to start a new global thread.",
                "Pick a project in the sidebar before sending if you want repository context.",
                "Reopen any thread tab to continue previous work.",
            },
            CodeAltaApp.BuildWelcomeGuidanceLines(null, globalScopeSelected: true).ToArray());

        CollectionAssert.AreEqual(
            new[]
            {
                "Use the prompt below to start a new thread for CodeAlta.",
                "Switch projects in the sidebar before sending if you want a different scope.",
                "Reopen any thread tab to continue previous work.",
            },
            CodeAltaApp.BuildWelcomeGuidanceLines(project, globalScopeSelected: false).ToArray());
    }

    [TestMethod]
    public void GetWelcomeFigletFont_LoadsEmbeddedAssetAndCachesInstance()
    {
        var first = CodeAltaApp.GetWelcomeFigletFont();
        var second = CodeAltaApp.GetWelcomeFigletFont();
        var lines = first.RenderLines("CodeAlta", new FigletRenderOptions { LetterSpacing = 1, TrimTrailingSpaces = true });

        Assert.AreSame(first, second);
        Assert.IsTrue(lines.Length > 0);
        Assert.IsTrue(lines.Any(static line => !string.IsNullOrWhiteSpace(line)));
    }

    [TestMethod]
    public void BuildWelcomeAltaGradientStops_UseMatchingEdgeColors()
    {
        var stops = CodeAltaApp.BuildWelcomeAltaGradientStops();

        Assert.AreEqual(11, stops.Length);
        Assert.AreEqual(0.00f, stops[0].Offset);
        Assert.AreEqual(0.50f, stops[5].Offset);
        Assert.AreEqual(1.00f, stops[^1].Offset);
        Assert.AreEqual(stops[0].Color, stops[^1].Color);
        Assert.AreEqual(stops[2].Color, stops[8].Color);
    }

    [TestMethod]
    public void ComputeLoopAnimationPhase_RepeatsAcrossCycleBoundary()
    {
        const long cycleTicks = TimeSpan.TicksPerSecond * 5L;

        var start = CodeAltaApp.ComputeLoopAnimationPhase(0, cycleTicks);
        var midpoint = CodeAltaApp.ComputeLoopAnimationPhase(cycleTicks / 2, cycleTicks);
        var end = CodeAltaApp.ComputeLoopAnimationPhase(cycleTicks, cycleTicks);

        Assert.AreEqual(0f, start, 0.0001f);
        Assert.AreEqual(0.5f, midpoint, 0.0001f);
        Assert.AreEqual(start, end, 0.0001f);
    }

    [TestMethod]
    public void BuildThinkingGradientStops_UseMultipleHighlightsAndLoopableEdges()
    {
        var stops = CodeAltaApp.BuildThinkingGradientStops();

        Assert.AreEqual(9, stops.Length);
        Assert.AreEqual(0.00f, stops[0].Offset);
        Assert.AreEqual(1.00f, stops[^1].Offset);
        Assert.AreEqual(stops[0].Color, stops[^1].Color);
    }

    [TestMethod]
    public void CreateInitialThreadHistoryLoadPlan_StartsAtLastUserPrompt()
    {
        var timestamp = DateTimeOffset.UtcNow;
        AgentEvent[] history =
        [
            new AgentContentDeltaEvent(AgentBackendIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(AgentBackendIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(AgentBackendIds.Copilot, "session-1", timestamp, null, AgentContentKind.Assistant, "assistant-1", null, "First reply"),
            new AgentContentDeltaEvent(AgentBackendIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(AgentBackendIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(AgentBackendIds.Copilot, "session-1", timestamp, null, AgentContentKind.Assistant, "assistant-2", null, "Second reply"),
        ];

        var plan = CodeAltaApp.CreateInitialThreadHistoryLoadPlan(history);

        Assert.AreEqual(3, plan.EventsToRender.Count);
        Assert.AreSame(history[3], plan.EventsToRender[0]);
        Assert.AreEqual(2, plan.OmittedMessageCount);
    }

    [TestMethod]
    public void BuildTruncatedHistoryTexts_UseHelpfulPluralization()
    {
        Assert.AreEqual("Load 1 previous message", CodeAltaApp.BuildTruncatedHistoryLoadButtonText(1));
        Assert.AreEqual("Load 12 previous messages", CodeAltaApp.BuildTruncatedHistoryLoadButtonText(12));
        Assert.AreEqual("1 previous message...", CodeAltaApp.BuildTruncatedHistorySummaryText(1));
        Assert.AreEqual("12 previous messages...", CodeAltaApp.BuildTruncatedHistorySummaryText(12));
    }

    [TestMethod]
    public async Task CreateTruncatedHistoryState_CanBeCreatedFromWorkerThread()
    {
        var state = await Task.Run(() => CodeAltaApp.CreateTruncatedHistoryState(3, static () => { }));

        Assert.IsNotNull(state);
        Assert.IsInstanceOfType<Rule>(state.Rule);
        Assert.IsInstanceOfType<Button>(state.Rule.CenterLabel);
        Assert.AreEqual(3, state.OmittedMessageCount);
    }

    [TestMethod]
    public void CreateDeferredUiAction_PostsWorkInsteadOfRunningInline()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(80, 20)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("root"),
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            });

        var invocationCount = 0;
        var action = CodeAltaApp.CreateDeferredUiAction(() => invocationCount++);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            action();
            Assert.AreEqual(0, invocationCount);

            TickTerminalApp(app);
            Assert.AreEqual(1, invocationCount);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void BuildInitialThreadHistoryItems_PrependsTruncatedHistoryBeforeFirstLoadedPrompt()
    {
        var pending = CodeAltaApp.CreatePendingChatMessage("hello");
        var followup = CodeAltaApp.CreatePendingChatMessage("assistant");
        var truncatedHistory = CodeAltaApp.CreateTruncatedHistoryState(3, static () => { });

        var items = CodeAltaApp.BuildInitialThreadHistoryItems(
            [pending.UserItem, followup.AssistantItem],
            truncatedHistory.Item);

        Assert.AreEqual(3, items.Count);
        Assert.AreSame(truncatedHistory.Item.Content, items[0].Content);
        Assert.AreSame(pending.UserItem.Content, items[1].Content);
        Assert.AreSame(followup.AssistantItem.Content, items[2].Content);
    }

    [TestMethod]
    public void BuildInitialThreadHistoryItems_LeavesRenderedItemsUnchangedWhenNoMarkerExists()
    {
        var pending = CodeAltaApp.CreatePendingChatMessage("hello");
        var followup = CodeAltaApp.CreatePendingChatMessage("assistant");

        var items = CodeAltaApp.BuildInitialThreadHistoryItems(
            [pending.UserItem, followup.AssistantItem],
            truncatedHistoryItem: null);

        Assert.AreEqual(2, items.Count);
        Assert.AreSame(pending.UserItem.Content, items[0].Content);
        Assert.AreSame(followup.AssistantItem.Content, items[1].Content);
    }

    [TestMethod]
    public void BuildWelcomePane_CreatesCenteredFigletLogo()
    {
        var welcome = CodeAltaApp.BuildWelcomePane(null, globalScopeSelected: true);

        var center = Assert.IsInstanceOfType<Center>(welcome);
        var stack = Assert.IsInstanceOfType<VStack>(center.Content);
        var logoCenter = Assert.IsInstanceOfType<Center>(stack.Children[0]);
        var logoRow = Assert.IsInstanceOfType<HStack>(logoCenter.Content);

        Assert.AreEqual(2, logoRow.Children.Count);
        Assert.AreEqual("Code", Assert.IsInstanceOfType<TextFiglet>(logoRow.Children[0]).Text);
        Assert.AreEqual("Alta", Assert.IsInstanceOfType<TextFiglet>(logoRow.Children[1]).Text);
    }

    [TestMethod]
    public void ShouldPromoteAgentEventToThinking_TracksLiveProgressButNotErrorsOrIdle()
    {
        var timestamp = DateTimeOffset.UtcNow;

        Assert.IsTrue(
            CodeAltaApp.ShouldPromoteAgentEventToThinking(
                new AgentContentDeltaEvent(
                    AgentBackendIds.Copilot,
                    "session-1",
                    timestamp,
                    null,
                    AgentContentKind.Reasoning,
                    "content-1",
                    null,
                    "Inspecting reconnect state...")));

        Assert.IsTrue(
            CodeAltaApp.ShouldPromoteAgentEventToThinking(
                new AgentActivityEvent(
                    AgentBackendIds.Copilot,
                    "session-1",
                    timestamp,
                    null,
                    AgentActivityKind.ToolCall,
                    AgentActivityPhase.Started,
                    "activity-1",
                    null,
                    "search",
                    "Searching...")));

        Assert.IsFalse(
            CodeAltaApp.ShouldPromoteAgentEventToThinking(
                new AgentSessionUpdateEvent(
                    AgentBackendIds.Copilot,
                    "session-1",
                    timestamp,
                    null,
                    AgentSessionUpdateKind.Idle,
                    "Idle")));

        Assert.IsFalse(
            CodeAltaApp.ShouldPromoteAgentEventToThinking(
                new AgentErrorEvent(
                    AgentBackendIds.Copilot,
                    "session-1",
                    timestamp,
                    "Reconnect failed.")));
    }

    [TestMethod]
    public void BuildReadyStatusText_ReflectsCurrentSelection()
    {
        var project = new ProjectDescriptor
        {
            Id = "project-1",
            Slug = "codealta",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\code\CodeAlta",
        };

        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        Assert.AreEqual("Prompt ready", CodeAltaApp.BuildReadyStatusText(null, null, globalScopeSelected: true));
        Assert.AreEqual("Prompt ready", CodeAltaApp.BuildReadyStatusText(null, project, globalScopeSelected: false));
        Assert.AreEqual("Prompt ready", CodeAltaApp.BuildReadyStatusText(thread, project, globalScopeSelected: false));
    }

    [TestMethod]
    public void BuildPromptUnavailablePlaceholder_UsesBackendStateAndSelection()
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        Assert.AreEqual(
            "Waiting for Codex to reconnect...",
            CodeAltaApp.BuildPromptUnavailablePlaceholder(thread, "Codex", ChatBackendAvailability.Connecting, anyBackendReady: false));
        Assert.AreEqual(
            "Install or connect Codex/Copilot to start a thread...",
            CodeAltaApp.BuildPromptUnavailablePlaceholder(null, "Codex", ChatBackendAvailability.Unsupported, anyBackendReady: false));
    }

    [TestMethod]
    public void BuildPromptUnavailableStatusText_DescribesConnectingAndMissingBackends()
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        Assert.AreEqual(
            "Reconnecting 'Review startup' to Codex. Prompt sending is temporarily unavailable.",
            CodeAltaApp.BuildPromptUnavailableStatusText(thread, "Codex", ChatBackendAvailability.Connecting, anyBackendReady: false));
        Assert.AreEqual(
            "No chat backend is connected. Browse threads and projects, but prompt sending is unavailable.",
            CodeAltaApp.BuildPromptUnavailableStatusText(null, "Codex", ChatBackendAvailability.Unsupported, anyBackendReady: false));
    }

    [TestMethod]
    public void ClassifyBackendInitializationFailure_TreatsMissingExecutableAsUnsupported()
    {
        var backendState = new ChatBackendState(AgentBackendIds.Codex, "Codex");

        var result = CodeAltaApp.ClassifyBackendInitializationFailure(
            backendState,
            new FileNotFoundException("codex executable was not found"));

        Assert.AreEqual(ChatBackendAvailability.Unsupported, result.Availability);
        StringAssert.Contains(result.StatusMessage, "Codex is unavailable");
    }

    [TestMethod]
    public void BuildStatusIconMarkup_ReturnsColoredIconsPerTone()
    {
        StringAssert.Contains(CodeAltaApp.BuildStatusIconMarkup(CodeAltaApp.StatusTone.Ready), NerdFont.MdCheckCircleOutline.ToString());
        StringAssert.Contains(CodeAltaApp.BuildStatusIconMarkup(CodeAltaApp.StatusTone.Warning), NerdFont.MdAlertOutline.ToString());
        StringAssert.Contains(CodeAltaApp.BuildStatusIconMarkup(CodeAltaApp.StatusTone.Error), NerdFont.MdAlertCircleOutline.ToString());
        StringAssert.Contains(CodeAltaApp.BuildStatusIconMarkup(CodeAltaApp.StatusTone.Info), NerdFont.OctInfo.ToString());
    }

    [TestMethod]
    public void CanLoadThreadHistory_AllowsRecoveredActiveSessionsWithoutStartedAt()
    {
        var recoverableThread = new WorkThreadDescriptor
        {
            ThreadId = "copilot:session-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Copilot.Value,
            BackendSessionId = "session-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Recovered Session",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
            StartedAt = null,
        };

        Assert.IsTrue(CodeAltaApp.CanLoadThreadHistory(recoverableThread));
    }

    [TestMethod]
    public void TryInferCopilotToolName_InfersReadAndGlobFromCompletionPayload()
    {
        var method = typeof(CodeAltaApp).GetMethod("TryInferCopilotToolName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        using var globJson = JsonDocument.Parse(
            """
            {
              "result": {
                "content": "C:\\code\\Tomlyn\\readme.md\nC:\\code\\Tomlyn\\src\\Tomlyn\\Tomlyn.csproj"
              }
            }
            """);
        object?[] globArgs = [globJson.RootElement.Clone(), null];
        var globResult = (bool)method.Invoke(null, globArgs)!;

        Assert.IsTrue(globResult);
        Assert.AreEqual("glob", globArgs[1]);

        using var readJson = JsonDocument.Parse(
            """
            {
              "result": {
                "content": "1. <Project>\n2.   <PropertyGroup>\n3.   </PropertyGroup>"
              }
            }
            """);
        object?[] readArgs = [readJson.RootElement.Clone(), null];
        var readResult = (bool)method.Invoke(null, readArgs)!;

        Assert.IsTrue(readResult);
        Assert.AreEqual("read", readArgs[1]);
    }

    [TestMethod]
    public void PreferToolDisplayName_KeepsExplicitCopilotStartNameWhenCompletionIsGeneric()
    {
        var method = typeof(CodeAltaApp).GetMethod("PreferToolDisplayName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        using var completionJson = JsonDocument.Parse(
            """
            {
              "toolCallId": "call-1",
              "result": {
                "content": "C:\\code\\Tomlyn\\readme.md"
              }
            }
            """);

        var completion = new AgentActivityEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Completed,
            "call-1",
            null,
            null,
            "done",
            completionJson.RootElement.Clone());

        var chosen = (string?)method.Invoke(null, ["view", "glob", completion]);

        Assert.AreEqual("view", chosen);
    }

    [TestMethod]
    public void ResolveToolArgumentText_FormatsCopilotArgumentsObject()
    {
        var method = typeof(CodeAltaApp).GetMethod("ResolveToolArgumentText", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        using var detailsJson = JsonDocument.Parse(
            """
            {
              "toolCallId": "call-1",
              "toolName": "glob",
              "arguments": {
                "path": "C:\\code\\Tomlyn",
                "pattern": "**/*.csproj"
              }
            }
            """);

        var activity = new AgentActivityEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Started,
            "call-1",
            null,
            "glob",
            null,
            detailsJson.RootElement.Clone());

        var arguments = (string?)method.Invoke(null, [activity]);

        Assert.IsNotNull(arguments);
        StringAssert.Contains(arguments, "\"path\": \"C:\\\\code\\\\Tomlyn\"");
        StringAssert.Contains(arguments, "\"pattern\": \"**/*.csproj\"");
    }

    [TestMethod]
    public void ResolveToolCommandText_UsesStructuredCodexFunctionArguments()
    {
        var method = typeof(CodeAltaApp).GetMethod("ResolveToolCommandText", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        using var detailsJson = JsonDocument.Parse(
            """
            {
              "name": "shell_command",
              "callId": "call-1",
              "arguments": {
                "command": "Get-Content C:\\code\\Tomlyn\\readme.md -TotalCount 250",
                "timeout_ms": 20000
              }
            }
            """);

        var activity = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Requested,
            "call-1",
            null,
            "shell_command",
            null,
            detailsJson.RootElement.Clone());

        var command = (string?)method.Invoke(null, [activity]);

        Assert.AreEqual(@"Get-Content C:\code\Tomlyn\readme.md -TotalCount 250", command);
    }

    [TestMethod]
    public void ResolveToolDisplayName_UsesCodexShellCommandVerb()
    {
        var method = typeof(CodeAltaApp)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "ResolveToolDisplayName" &&
                         m.GetParameters().Length == 1 &&
                         m.GetParameters()[0].ParameterType == typeof(AgentActivityEvent));
        Assert.IsNotNull(method);

        using var detailsJson = JsonDocument.Parse(
            """
            {
              "name": "shell_command",
              "callId": "call-1",
              "arguments": {
                "command": "Get-Content C:\\code\\Tomlyn\\readme.md -TotalCount 250",
                "timeout_ms": 20000
              }
            }
            """);

        var activity = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Requested,
            "call-1",
            null,
            "shell_command",
            null,
            detailsJson.RootElement.Clone());

        var displayName = (string?)method.Invoke(null, [activity]);

        Assert.AreEqual("Get-Content", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_PreservesMeaningfulDotnetSubcommand()
    {
        var method = typeof(CodeAltaApp).GetMethod("ExtractCommandDisplayName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var displayName = (string?)method.Invoke(null, ["dotnet test CodeAlta.Tests/CodeAlta.Tests.csproj -c Release"]);

        Assert.AreEqual("dotnet test", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_PreservesMeaningfulGitSubcommand()
    {
        var method = typeof(CodeAltaApp).GetMethod("ExtractCommandDisplayName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var displayName = (string?)method.Invoke(null, ["git status --short"]);

        Assert.AreEqual("git status", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_SkipsFlagsForSecondLevelGitCommand()
    {
        var method = typeof(CodeAltaApp).GetMethod("ExtractCommandDisplayName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var displayName = (string?)method.Invoke(null, ["git remote -v"]);

        Assert.AreEqual("git remote", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_DoesNotTreatFlagsOrPathsAsSubcommands()
    {
        var method = typeof(CodeAltaApp).GetMethod("ExtractCommandDisplayName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        var listDisplayName = (string?)method.Invoke(null, ["ls -al"]);
        var copyDisplayName = (string?)method.Invoke(null, ["cp /mnt/c/source.txt /mnt/c/dest.txt"]);

        Assert.AreEqual("ls", listDisplayName);
        Assert.AreEqual("cp", copyDisplayName);
    }

    [TestMethod]
    public void BuildToolCallSummaryMarkup_OmitsRawJsonArgumentPreview()
    {
        var entry = new ToolCallEntryState(
            "call-1",
            new Button(new TextBlock("tool")),
            new Markup("tool"))
        {
            ActivityKind = AgentActivityKind.ToolCall,
            Status = ToolCallDisplayStatus.Running,
            DisplayName = "glob",
            ArgumentText =
                """
                {
                  "timeout_ms": 30000,
                  "justification": "Check repo status"
                }
                """,
        };

        var markup = ToolCallSummaryFormatter.BuildSummaryMarkup(entry);

        Assert.IsNotNull(markup);
        Assert.IsFalse(markup.Contains("{", StringComparison.Ordinal));
        StringAssert.Contains(markup, "glob");
    }

    [TestMethod]
    public void FormatChatContentMarkdown_SanitizesInlineImagePayloads()
    {
        var markdown = CodeAltaApp.FormatChatContentMarkdown(
            AgentContentKind.User,
            $"Please inspect this.{Environment.NewLine}<image>{Environment.NewLine}data:image/png;base64,AAAA");

        Assert.AreEqual($"Please inspect this.{Environment.NewLine}Inline Image", markdown);
        Assert.IsFalse(markdown.Contains("data:image", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FormatChatCardTimestamp_UsesInvariantReadableFormat()
    {
        var timestamp = new DateTimeOffset(2026, 03, 12, 14, 5, 6, TimeSpan.FromHours(1));

        var text = CodeAltaApp.FormatChatCardTimestamp(timestamp);

        Assert.AreEqual("2026-03-12 14:05:06", text);
    }

    [TestMethod]
    public async Task ApplyChatCardTimestamp_CanBeCalledFromWorkerThread()
    {
        var timestamp = new DateTimeOffset(2026, 03, 12, 14, 5, 6, TimeSpan.FromHours(1));
        var markup = new Markup();

        await Task.Run(() => CodeAltaApp.ApplyChatCardTimestamp(markup, timestamp));

        Assert.AreEqual("[dim]2026-03-12 14:05:06[/]", markup.Text);
    }

    [TestMethod]
    public void ResolveCompletedThreadContent_PreservesBufferedDeltaWhenCompletedPayloadIsEmpty()
    {
        var buffer = new System.Text.StringBuilder("Streaming assistant reply");

        var content = CodeAltaApp.ResolveCompletedThreadContent(string.Empty, buffer);

        Assert.AreEqual("Streaming assistant reply", content);
    }

    [TestMethod]
    public void ResolveCompletedThreadContent_PrefersCompletedPayloadWhenPresent()
    {
        var buffer = new System.Text.StringBuilder("Older delta text");

        var content = CodeAltaApp.ResolveCompletedThreadContent("Final assistant reply", buffer);

        Assert.AreEqual("Final assistant reply", content);
    }

    [TestMethod]
    public void ShouldRunInlineOnCurrentThread_AllowsBootstrapThreadBeforeTerminalStarts()
    {
        Assert.IsTrue(CodeAltaApp.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: false,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaApp.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: true,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaApp.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: true,
            terminalLoopStarted: true));

        Assert.IsFalse(CodeAltaApp.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: false,
            terminalLoopStarted: true));
    }

    [TestMethod]
    public void CompactSidebarThreadTitle_TrimsLongTitlesToSingleLineLength()
    {
        var compact = CodeAltaApp.CompactSidebarThreadTitle("The lunet-build action in this repository is used like this:");

        Assert.AreEqual("The lunet-build action in this re…", compact);
        Assert.IsFalse(compact.Contains('\n'));
    }

    [TestMethod]
    public void BuildThreadSidebarTooltip_PreservesFullTitleAndSummary()
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review Tomlyn update",
            LatestSummary = "Check the parser changes and resulting tests.",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        var tooltip = CodeAltaApp.BuildThreadSidebarTooltip(thread);

        StringAssert.Contains(tooltip, "Review Tomlyn update");
        StringAssert.Contains(tooltip, "Check the parser changes and resulting tests.");
    }

    [TestMethod]
    public void ResolveInitialSelection_DefersSelectedThreadRestoreUntilUiLoopStarts()
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Investigate startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        var selection = CodeAltaApp.ResolveInitialSelection(
            new WorkThreadViewState
            {
                OpenThreadIds = ["thread-1"],
                SelectedThreadId = "thread-1",
            },
            [thread]);

        Assert.AreEqual("thread-1", selection.SelectedThreadId);
        Assert.AreEqual("thread-1", selection.StartupThreadRestoreId);
    }

    [TestMethod]
    public void FormatChatRawEventMarkdown_RendersBackendEventTypeAndPayload()
    {
        using var payloadJson = JsonDocument.Parse("""{"kind":"shell","toolCallId":"call-1"}""");
        var markdown = CodeAltaApp.FormatChatRawEventMarkdown(
            new AgentRawEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                "permission.request",
                payloadJson.RootElement.Clone()));

        StringAssert.Contains(markdown, "Event");
        StringAssert.Contains(markdown, "`permission.request`");
        StringAssert.Contains(markdown, "\"kind\":\"shell\"");
        StringAssert.Contains(markdown, "\"toolCallId\":\"call-1\"");
    }

    [TestMethod]
    public void ShouldDisplayActivity_HidesTurnAndToolStartNoise()
    {
        var turn = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.Turn,
            AgentActivityPhase.Started,
            "turn-1",
            null,
            "assistant turn",
            null);
        var toolStart = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Started,
            "tool-1",
            null,
            "view",
            "Reading file");
        var toolCompleted = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Completed,
            "tool-1",
            null,
            "view",
            "Done");

        Assert.IsFalse(CodeAltaApp.ShouldDisplayActivity(turn));
        Assert.IsFalse(CodeAltaApp.ShouldDisplayActivity(toolStart));
        Assert.IsFalse(CodeAltaApp.ShouldDisplayActivity(toolCompleted));
    }

    [TestMethod]
    public void ShouldDisplayActivity_HidesToolGroupedSubagentLifecycleAndKeepsCompaction()
    {
        var subagent = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.Subagent,
            AgentActivityPhase.Started,
            "subagent-1",
            null,
            "reviewer",
            "Started reviewer agent");
        var compaction = new AgentActivityEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.Compaction,
            AgentActivityPhase.Completed,
            "compaction-1",
            null,
            null,
            "Compaction completed.");

        Assert.IsFalse(CodeAltaApp.ShouldDisplayActivity(subagent));
        Assert.IsTrue(CodeAltaApp.ShouldDisplayActivity(compaction));
    }

    [TestMethod]
    public void ShouldDisplayRawEvent_HidesRawEventsFromTimeline()
    {
        using var payloadJson = JsonDocument.Parse("""{"kind":"shell"}""");
        var raw = new AgentRawEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            "tool.started",
            payloadJson.RootElement.Clone());

        Assert.IsFalse(CodeAltaApp.ShouldDisplayRawEvent(raw));
    }

    [TestMethod]
    public void ShouldDisplaySessionUpdate_OnlyShowsWarnings()
    {
        var copilotUsage = new AgentSessionUpdateEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.UsageUpdated,
            "token usage");
        var copilotResumed = new AgentSessionUpdateEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.Resumed,
            "session resumed");
        var copilotWarning = new AgentSessionUpdateEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.Warning,
            "warning");
        var codexUsage = new AgentSessionUpdateEvent(
            AgentBackendIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.UsageUpdated,
            "token usage");

        Assert.IsFalse(CodeAltaApp.ShouldDisplaySessionUpdate(copilotUsage));
        Assert.IsFalse(CodeAltaApp.ShouldDisplaySessionUpdate(copilotResumed));
        Assert.IsTrue(CodeAltaApp.ShouldDisplaySessionUpdate(copilotWarning));
        Assert.IsFalse(CodeAltaApp.ShouldDisplaySessionUpdate(codexUsage));
    }

    [TestMethod]
    public void ShouldDisplayPermissionRequest_HidesAutoApprovedPermissions()
    {
        Assert.IsFalse(CodeAltaApp.ShouldDisplayPermissionRequest(autoApproveEnabled: true));
        Assert.IsTrue(CodeAltaApp.ShouldDisplayPermissionRequest(autoApproveEnabled: false));
    }

    [TestMethod]
    public void ShouldDisplayInteraction_HidesAutoApprovedPermissionResolutions()
    {
        var permissionResolved = new AgentInteractionEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentInteractionKind.PermissionResolved,
            "interaction-1",
            "Permission resolved.");
        var userInputResolved = new AgentInteractionEvent(
            AgentBackendIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentInteractionKind.UserInputResolved,
            "interaction-2",
            "Input resolved.");

        Assert.IsFalse(CodeAltaApp.ShouldDisplayInteraction(permissionResolved, autoApproveEnabled: true));
        Assert.IsTrue(CodeAltaApp.ShouldDisplayInteraction(permissionResolved, autoApproveEnabled: false));
        Assert.IsTrue(CodeAltaApp.ShouldDisplayInteraction(userInputResolved, autoApproveEnabled: true));
    }

    [TestMethod]
    public void FormatChatPermissionRequestMarkdown_RendersTypedAndGenericDetails()
    {
        var typedMarkdown = CodeAltaApp.FormatChatPermissionRequestMarkdown(
            new AgentCommandPermissionRequest(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                "interaction-1",
                ApprovalId: null,
                Command: "dotnet test",
                WorkingDirectory: @"C:\repo",
                Actions: null,
                Reason: "Run the regression suite.",
                Network: new AgentNetworkAccessRequest("api.github.com", "https"),
                ProposedExecPolicyAmendment: null,
                ProposedNetworkPolicyAmendments: null));

        StringAssert.Contains(typedMarkdown, "_The agent is blocked");
        StringAssert.Contains(typedMarkdown, "```shell");
        StringAssert.Contains(typedMarkdown, "dotnet test");
        StringAssert.Contains(typedMarkdown, "`C:\\repo`");
        StringAssert.Contains(typedMarkdown, "https://api.github.com");

        using var rawJson = JsonDocument.Parse("""{"toolName":"search_workspace"}""");
        var genericMarkdown = CodeAltaApp.FormatChatPermissionRequestMarkdown(
            new AgentGenericPermissionRequest(
                AgentBackendIds.Copilot,
                "session-2",
                DateTimeOffset.UtcNow,
                null,
                "interaction-2",
                "custom-tool",
                rawJson.RootElement.Clone()));

        StringAssert.Contains(genericMarkdown, "custom-tool");
        StringAssert.Contains(genericMarkdown, "`search_workspace`");
    }

    [TestMethod]
    public void FormatChatUserInputRequestMarkdown_ByDefault_DescribesCodeAltaAutoAnswering()
    {
        var markdown = CodeAltaApp.FormatChatUserInputRequestMarkdown(
            new AgentUserInputRequest(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                "interaction-1",
                new AgentUserInputForm(
                    [
                        new AgentUserInputPrompt(
                            Id: "answer",
                            Question: "Which option do you prefer?",
                            Header: "Pick one",
                            Options:
                            [
                                new AgentUserInputOption("Search first", "Inspect the repository before answering."),
                                new AgentUserInputOption("Answer directly", "Respond from prior knowledge."),
                            ],
                            AllowFreeform: false)
                    ])));

        StringAssert.Contains(markdown, "CodeAlta will prefer continue/inspect-style choices");
        StringAssert.Contains(markdown, "Which option do you prefer?");
        StringAssert.Contains(markdown, "Search first");
        StringAssert.Contains(markdown, "Answer directly");
        StringAssert.Contains(markdown, "Freeform: disabled");
    }

    [TestMethod]
    public void FormatChatUserInputRequestMarkdown_WhenAutoApproveDisabled_DescribesImplementationGap()
    {
        var markdown = CodeAltaApp.FormatChatUserInputRequestMarkdown(
            new AgentUserInputRequest(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                "interaction-1",
                new AgentUserInputForm(
                    [
                        new AgentUserInputPrompt(
                            Id: "answer",
                            Question: "Pick one",
                            Options: [new AgentUserInputOption("choice-a"), new AgentUserInputOption("choice-b")],
                            AllowFreeform: false)
                    ])),
            autoApprove: false);

        StringAssert.Contains(markdown, "Terminal question prompts are not implemented yet");
    }

    [TestMethod]
    public void CreateChatUserInputResponse_WhenAutoApproveEnabled_SelectsDefaultAnswers()
    {
        var response = CodeAltaApp.CreateChatUserInputResponse(
            new AgentUserInputRequest(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                "interaction-1",
                new AgentUserInputForm(
                    [
                        new AgentUserInputPrompt(
                            Id: "choice",
                            Question: "Pick one",
                            Options: [new AgentUserInputOption("choice-a"), new AgentUserInputOption("choice-b")],
                            AllowFreeform: false),
                        new AgentUserInputPrompt(
                            Id: "freeform",
                            Question: "Anything else?",
                            AllowFreeform: true),
                        new AgentUserInputPrompt(
                            Id: "secret",
                            Question: "Provide a secret",
                            AllowFreeform: true,
                            IsSecret: true),
                    ])),
            autoApprove: true);

        Assert.AreEqual("choice-a", response.Answers["choice"]);
        Assert.AreEqual("No preference. Use your best judgment and continue.", response.Answers["freeform"]);
        Assert.AreEqual(string.Empty, response.Answers["secret"]);
    }

    [TestMethod]
    public void CreateChatUserInputResponse_WhenChoicesIncludePositiveAndNegativeOptions_PrefersProceeding()
    {
        var response = CodeAltaApp.CreateChatUserInputResponse(
            new AgentUserInputRequest(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                "interaction-1",
                new AgentUserInputForm(
                    [
                        new AgentUserInputPrompt(
                            Id: "choice",
                            Question: "Do you want me to continue with the tool call?",
                            Options:
                            [
                                new AgentUserInputOption("Reject the tool call"),
                                new AgentUserInputOption("Continue and inspect the project"),
                                new AgentUserInputOption("Provide instructions instead"),
                            ],
                            AllowFreeform: false)
                    ])),
            autoApprove: true);

        Assert.AreEqual("Continue and inspect the project", response.Answers["choice"]);
    }

    [TestMethod]
    public void CreateChatUserInputResponse_WhenAutoApproveDisabled_ReturnsEmptyAnswers()
    {
        var response = CodeAltaApp.CreateChatUserInputResponse(
            new AgentUserInputRequest(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                "interaction-1",
                new AgentUserInputForm(
                    [
                        new AgentUserInputPrompt(
                            Id: "choice",
                            Question: "Pick one",
                            Options: [new AgentUserInputOption("choice-a"), new AgentUserInputOption("choice-b")],
                            AllowFreeform: false)
                    ])),
            autoApprove: false);

        Assert.AreEqual(string.Empty, response.Answers["choice"]);
    }

    [TestMethod]
    public void FormatChatInteractionResolutionMarkdown_CanProduceFooter()
    {
        using var detailsJson = JsonDocument.Parse("""{"decisionKind":"AllowOnce"}""");
        var markdown = CodeAltaApp.FormatChatInteractionResolutionMarkdown(
            new AgentInteractionEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentInteractionKind.PermissionResolved,
                "interaction-1",
                "Permission resolved: AllowOnce.",
                detailsJson.RootElement.Clone()),
            includeHeading: false);

        StringAssert.Contains(markdown, "_Status:_ Permission resolved: AllowOnce.");
        StringAssert.Contains(markdown, "Decision: Allow Once");
    }

    [TestMethod]
    public void FormatChatImmediatePermissionDecisionMarkdown_ShowsCodeAltaResponse()
    {
        var markdown = CodeAltaApp.FormatChatImmediatePermissionDecisionMarkdown(
            new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce),
            autoApprove: true);

        StringAssert.Contains(markdown, "CodeAlta response: auto-approved");
        StringAssert.Contains(markdown, "Decision: Allow Once");
    }

    [TestMethod]
    public void FormatChatImmediateUserInputResponseMarkdown_ShowsReturnedAnswer()
    {
        var markdown = CodeAltaApp.FormatChatImmediateUserInputResponseMarkdown(
            new AgentUserInputResponse(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["answer"] = "Look in C:\\code for projects",
                }),
            autoApprove: true);

        StringAssert.Contains(markdown, "CodeAlta auto-answered");
        StringAssert.Contains(markdown, "`answer`");
        StringAssert.Contains(markdown, "Look in C:\\code for projects");
    }

    [TestMethod]
    public void FormatChatInteractionResolutionMarkdown_NotesBlankUserInputAnswers()
    {
        using var detailsJson = JsonDocument.Parse("""{"answerCount":1,"answers":{"answer":""}}""");
        var markdown = CodeAltaApp.FormatChatInteractionResolutionMarkdown(
            new AgentInteractionEvent(
                AgentBackendIds.Copilot,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentInteractionKind.UserInputResolved,
                "interaction-1",
                "User input resolved (1 answer(s)).",
                detailsJson.RootElement.Clone()),
            includeHeading: false);

        StringAssert.Contains(markdown, "_Status:_ User input resolved (1 answer(s)).");
        StringAssert.Contains(markdown, "`answer`");
        StringAssert.Contains(markdown, "_empty_");
        StringAssert.Contains(markdown, "Terminal question prompts are not implemented yet");
    }

    [TestMethod]
    public void BuildChatReasoningOptions_UsesSupportedEffortsOnly()
    {
        var options = CodeAltaApp.BuildChatReasoningOptions(
            new AgentModelInfo(
                "model-a",
                SupportedReasoningEfforts: [AgentReasoningEffort.Minimal, AgentReasoningEffort.High]));

        CollectionAssert.AreEqual(
            new[] { "Minimal", "High" },
            options.Select(static option => option.Label).ToArray());
    }

    [TestMethod]
    public void ResolvePreferredReasoningEffort_PrefersHighWhenSupportedAndNoPreferenceIsSet()
    {
        var effort = CodeAltaApp.ResolvePreferredReasoningEffort(
            new AgentModelInfo(
                "gpt-5.4",
                DefaultReasoningEffort: AgentReasoningEffort.Medium,
                SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]),
            preferredReasoningEffort: null);

        Assert.AreEqual(AgentReasoningEffort.High, effort);
    }

    [TestMethod]
    public void ResolvePreferredReasoningEffort_PreservesRequestedEffortWhenSupported()
    {
        var effort = CodeAltaApp.ResolvePreferredReasoningEffort(
            new AgentModelInfo(
                "gpt-5.4",
                DefaultReasoningEffort: AgentReasoningEffort.High,
                SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]),
            preferredReasoningEffort: AgentReasoningEffort.Low);

        Assert.AreEqual(AgentReasoningEffort.Low, effort);
    }

    [TestMethod]
    public void ResolvePreferredModelId_FallsBackToFirstAvailableModel()
    {
        AgentModelInfo[] models =
        [
            new("gpt-5.4"),
            new("gpt-5-mini"),
        ];

        var selectedModelId = CodeAltaApp.ResolvePreferredModelId(models, "missing-model");

        Assert.AreEqual("gpt-5.4", selectedModelId);
    }

    [TestMethod]
    public void ShouldDisplayCompletedContent_HidesEmptyReasoning()
    {
        var completed = new AgentContentCompletedEvent(
            AgentBackendIds.Codex,
            "thread-1",
            DateTimeOffset.Parse("2026-03-14T14:02:50+00:00"),
            new AgentRunId("turn-1"),
            AgentContentKind.Reasoning,
            "reasoning-1",
            null,
            string.Empty);

        Assert.IsFalse(CodeAltaApp.ShouldDisplayCompletedContent(completed));
    }

    [TestMethod]
    public void ShouldDisplayCompletedContent_HidesCommandOutput()
    {
        var completed = new AgentContentCompletedEvent(
            AgentBackendIds.Codex,
            "thread-1",
            DateTimeOffset.Parse("2026-03-14T14:02:50+00:00"),
            new AgentRunId("turn-1"),
            AgentContentKind.CommandOutput,
            "command-1",
            null,
            "Exit code: 0");

        Assert.IsFalse(CodeAltaApp.ShouldDisplayCompletedContent(completed));
    }

    [TestMethod]
    public void ShouldDisplayContentDelta_HidesCommandOutput()
    {
        var delta = new AgentContentDeltaEvent(
            AgentBackendIds.Codex,
            "thread-1",
            DateTimeOffset.Parse("2026-03-14T14:02:50+00:00"),
            new AgentRunId("turn-1"),
            AgentContentKind.CommandOutput,
            "command-1",
            null,
            "partial output");

        Assert.IsFalse(CodeAltaApp.ShouldDisplayContentDelta(delta));
    }

    [TestMethod]
    public void BuildChatBackendStatusMarkup_IncludesWarningsAndSelectedBackend()
    {
        var states = new[]
        {
            new ChatBackendState(AgentBackendIds.Codex, "Codex")
            {
                Availability = ChatBackendAvailability.Ready,
                StatusMessage = "Connected · 2 models",
            },
            new ChatBackendState(AgentBackendIds.Copilot, "Copilot")
            {
                Availability = ChatBackendAvailability.Unsupported,
                StatusMessage = "Copilot is unavailable: CLI not found.",
            },
        };

        var markup = CodeAltaApp.BuildChatBackendStatusMarkup(states, AgentBackendIds.Codex, isInitializing: false);

        StringAssert.Contains(markup, "Codex");
        StringAssert.Contains(markup, "Copilot");
        Assert.IsFalse(markup.Contains("CLI not found", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionUsageFormatting_UsesInvariantCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            var usage = new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(50000, 120000, 12, "Active context window"),
                Scope: AgentUsageScope.CurrentWindow,
                Source: AgentUsageSource.CopilotSessionUsageInfo,
                UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:00:00+00:00"));

            var indicator = CodeAltaApp.BuildSessionUsageIndicatorMarkup(usage);
            var summary = CodeAltaApp.FormatSessionUsageSummary(usage);

            Assert.AreEqual("[dim]Context[/] [success]42%[/]", indicator);
            Assert.AreEqual("50,000 / 120,000 tokens (41.7%)", summary);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [TestMethod]
    public void FormatSessionUsageSummary_CodexWindowUsesNormalizedWindowSnapshot()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(40473, 258400, null, "Active context window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CodexThreadTokenUsageUpdated,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:48:22+00:00"),
            Details: new CodexSessionUsageDetails(
                LastTurnUsage: new CodexTokenUsage(40064, 40283, 190, 33, 40473),
                TotalUsage: new CodexTokenUsage(32809344, 35409515, 161773, 67166, 35571288),
                ModelContextWindow: 258400));

        var indicator = CodeAltaApp.BuildSessionUsageIndicatorMarkup(usage);
        var summary = CodeAltaApp.FormatSessionUsageSummary(usage);

        Assert.AreEqual("[dim]Context[/] [success]16%[/]", indicator);
        Assert.AreEqual("40,473 / 258,400 tokens (15.7%)", summary);
    }

    [TestMethod]
    public void BuildSessionUsageIndicatorMarkup_CodexWindowUsesLastTurnTotals()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(200535, 258400, null, "Active context window"),
            LastOperation: new AgentOperationUsageSnapshot(
                InputTokens: 200435,
                OutputTokens: 86,
                CachedInputTokens: 199424,
                ReasoningTokens: 14,
                Label: "Last turn"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CodexThreadTokenUsageUpdated,
            UpdatedAt: DateTimeOffset.Parse("2026-03-19T05:59:24+00:00"),
            Details: new CodexSessionUsageDetails(
                LastTurnUsage: new CodexTokenUsage(199424, 200435, 86, 14, 200535),
                TotalUsage: new CodexTokenUsage(31249920, 33493301, 148132, 61352, 33641433),
                ModelContextWindow: 258400));

        var indicator = CodeAltaApp.BuildSessionUsageIndicatorMarkup(usage);
        var markdown = CodeAltaApp.BuildSessionUsageMarkdown(usage, "Codex", "gpt-5.4");

        Assert.AreEqual("[dim]Context[/] [warning]78%[/]", indicator);
        StringAssert.Contains(markdown, "Window: 200,535 / 258,400 tokens (77.6%)");
        StringAssert.Contains(markdown, "Thread total: total 33,641,433");
    }

    [TestMethod]
    public void BuildSessionUsageIndicatorMarkup_UsesErrorToneNearWindowLimit()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(95000, 100000, null, "Active context window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CopilotSessionUsageInfo,
            UpdatedAt: DateTimeOffset.Parse("2026-03-19T08:00:00+00:00"));

        var indicator = CodeAltaApp.BuildSessionUsageIndicatorMarkup(usage);

        Assert.AreEqual("[dim]Context[/] [error]95%[/]", indicator);
    }

    [TestMethod]
    public void MergeSessionUsage_MergesTypedBackendDetails()
    {
        var current = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(4096, 128000, 8, "Active thread window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CodexThreadTokenUsageUpdated,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T20:00:00+00:00"),
            Details: new CodexSessionUsageDetails(
                LastTurnUsage: new CodexTokenUsage(10, 100, 20, 5, 125)));
        var incoming = new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(
                InputTokens: 1000,
                OutputTokens: 250,
                CachedInputTokens: 40,
                ReasoningTokens: 30,
                Label: "Last turn"),
            RateLimits: new AgentRateLimitSummary(
                Name: "Requests",
                PlanType: "Pro",
                Primary: new AgentRateLimitWindow(61, null, 60),
                Label: "Account rate limits"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CodexTokenCountEvent,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T20:05:00+00:00"),
            Details: new CodexSessionUsageDetails(
                TotalUsage: new CodexTokenUsage(40, 1000, 250, 30, 1320),
                RateLimits: new CodexRateLimitSnapshot(
                    "requests",
                    "Requests",
                    "Pro",
                    new CodexRateLimitWindow(61, null, 60),
                    null)));

        var merged = CodeAltaApp.MergeSessionUsage(current, incoming);

        Assert.AreEqual(4096L, merged.CurrentTokens);
        Assert.AreEqual(128000L, merged.TokenLimit);
        Assert.AreEqual(8, merged.MessageCount);
        Assert.IsNotNull(merged.LastOperation);
        Assert.AreEqual(1000L, merged.LastOperation.InputTokens);
        Assert.IsNotNull(merged.RateLimits);
        Assert.AreEqual(61, merged.RateLimits.Primary!.UsedPercent);
        Assert.AreEqual(AgentUsageSource.CodexTokenCountEvent, merged.Source);
        var details = Assert.IsInstanceOfType<CodexSessionUsageDetails>(merged.Details);
        Assert.AreEqual(125L, details.LastTurnUsage!.TotalTokens);
        Assert.AreEqual(1320L, details.TotalUsage!.TotalTokens);
        Assert.AreEqual(61, details.RateLimits!.Primary!.UsedPercent);
    }

    [TestMethod]
    public void MergeSessionUsage_MergesCopilotQuotaSnapshotsWithoutDroppingWindow()
    {
        var current = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(12345, 200000, 18, "Active context window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CopilotSessionUsageInfo,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T20:00:00+00:00"),
            Details: new CopilotSessionUsageDetails(
                LastAssistantUsage: new CopilotAssistantUsage("gpt-5.4", InputTokens: 1000, OutputTokens: 200)));
        var incoming = new AgentSessionUsage(
            LastOperation: new AgentOperationUsageSnapshot(
                Model: "gpt-5.4",
                InputTokens: 1200,
                OutputTokens: 300,
                Label: "Last API call"),
            Scope: AgentUsageScope.LastOperation,
            Source: AgentUsageSource.CopilotAssistantUsage,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T20:05:00+00:00"),
            Details: new CopilotSessionUsageDetails(
                QuotaSnapshots:
                [
                    new CopilotQuotaSnapshot(
                        "chat",
                        new CopilotRequestQuotaDetails(EntitlementRequests: 1500, UsedRequests: 477)),
                ]));

        var merged = CodeAltaApp.MergeSessionUsage(current, incoming);

        Assert.AreEqual(12345L, merged.CurrentTokens);
        Assert.AreEqual(200000L, merged.TokenLimit);
        Assert.AreEqual(18, merged.MessageCount);
        Assert.IsNotNull(merged.LastOperation);
        Assert.AreEqual(1200L, merged.LastOperation.InputTokens);
        var details = Assert.IsInstanceOfType<CopilotSessionUsageDetails>(merged.Details);
        Assert.AreEqual(1, details.QuotaSnapshots!.Length);
        Assert.IsInstanceOfType<CopilotRequestQuotaDetails>(details.QuotaSnapshots[0].Details);
        Assert.IsNotNull(details.LastAssistantUsage);
    }

    [TestMethod]
    public void BuildSessionUsageMarkdown_UsesInvariantCultureAndSections()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            var markdown = CodeAltaApp.BuildSessionUsageMarkdown(
                new AgentSessionUsage(
                    Window: new AgentWindowUsageSnapshot(50000, 120000, 12, "Active context window"),
                    LastOperation: new AgentOperationUsageSnapshot(
                        InputTokens: 1000,
                        OutputTokens: 200,
                        CachedInputTokens: 25,
                        ReasoningTokens: 10,
                        Label: "Last turn"),
                    RateLimits: new AgentRateLimitSummary(
                        Name: "Requests",
                        PlanType: "Pro",
                        Primary: new AgentRateLimitWindow(42, null, 60),
                        Label: "Account rate limits"),
                    Scope: AgentUsageScope.CurrentWindow,
                    Source: AgentUsageSource.CodexTokenCountEvent,
                    UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:08:00+00:00"),
                    Details: new CodexSessionUsageDetails(
                        LastTurnUsage: new CodexTokenUsage(25, 1000, 200, 10, 1235),
                        TotalUsage: new CodexTokenUsage(200, 5000, 800, 40, 6040),
                        RateLimits: new CodexRateLimitSnapshot(
                            "requests",
                            "Requests",
                            "Pro",
                            new CodexRateLimitWindow(42, null, 60),
                            null))),
                "Codex",
                "gpt-5-codex");

            StringAssert.Contains(markdown, "# Codex context usage");
            StringAssert.Contains(markdown, "## Usage breakdown: 12 messages");
            StringAssert.Contains(markdown, "## Limits");
            StringAssert.Contains(markdown, "## Backend-specific details");
            StringAssert.Contains(markdown, "50,000 / 120,000 tokens (41.7%)");
            StringAssert.Contains(markdown, "Last turn: input 1,000");
            StringAssert.Contains(markdown, "42% used");
            Assert.IsFalse(markdown.Contains("## Summary", StringComparison.Ordinal));
            Assert.IsFalse(markdown.Contains("Codex limit identity", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [TestMethod]
    public void BuildSessionUsageMarkdown_CopilotQuotaSnapshotsUseTypedSummaries()
    {
        var markdown = CodeAltaApp.BuildSessionUsageMarkdown(
            new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(49652, 272000, 54, "Active context window"),
                LastOperation: new AgentOperationUsageSnapshot(
                    Model: "gpt-5.4",
                    ReasoningEffort: "high",
                    Initiator: "agent",
                    InputTokens: 47307,
                    OutputTokens: 989,
                    CacheReadTokens: 47104,
                    CacheWriteTokens: 0,
                    Label: "Last API call"),
                Scope: AgentUsageScope.LastOperation,
                Source: AgentUsageSource.CopilotAssistantUsage,
                UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:39:53+00:00"),
                Details: new CopilotSessionUsageDetails(
                    LastAssistantUsage: new CopilotAssistantUsage(
                        Model: "gpt-5.4",
                        ReasoningEffort: "high",
                        Initiator: "agent",
                        InputTokens: 47307,
                        OutputTokens: 989,
                        CacheReadTokens: 47104,
                        CacheWriteTokens: 0,
                        DurationMs: null,
                        Cost: null,
                        TotalNanoAiu: null),
                    QuotaSnapshots:
                    [
                        new CopilotQuotaSnapshot(
                            "premium_interactions",
                            new CopilotRequestQuotaDetails(
                                EntitlementRequests: 1500,
                                UsedRequests: 477,
                                UsageAllowedWithExhaustion: true,
                                IsUnlimitedEntitlement: false)),
                    ])),
            "Copilot",
            "gpt-5.4");

        StringAssert.Contains(markdown, "# Copilot context usage");
        StringAssert.Contains(markdown, "## Quotas");
        StringAssert.Contains(markdown, "### Copilot quota snapshots");
        StringAssert.Contains(markdown, "| Quota | Usage | Status |");
        StringAssert.Contains(markdown, "| premium_interactions | 477 / 1,500");
        StringAssert.Contains(markdown, "allowed |");
    }

    [TestMethod]
    public void FormatOperationPopupText_UsesMetadataOnlyWhenChartExists()
    {
        var popupText = CodeAltaApp.FormatOperationPopupText(
            new AgentOperationUsageSnapshot(
                Model: "gpt-5.4",
                ReasoningEffort: "high",
                Initiator: "agent",
                InputTokens: 103252,
                OutputTokens: 234,
                CachedInputTokens: 99968,
                ReasoningTokens: 164,
                DurationMs: 45152,
                Label: "Last API call"));

        Assert.AreEqual("gpt-5.4 · effort high · initiator agent · duration 45152 ms", popupText);
    }

    [TestMethod]
    public void FormatOperationPopupText_OmitsCodexTokenSummaryWhenChartExists()
    {
        var popupText = CodeAltaApp.FormatOperationPopupText(
            new AgentOperationUsageSnapshot(
                InputTokens: 103252,
                OutputTokens: 234,
                CachedInputTokens: 99968,
                ReasoningTokens: 164,
                Label: "Last turn"));

        Assert.IsNull(popupText);
    }

    [TestMethod]
    public void ResolveChatBackendSelection_CanPreserveCurrentSelection()
    {
        var selected = CodeAltaApp.ResolveChatBackendSelection(
            AgentBackendIds.Copilot,
            AgentBackendIds.Codex,
            adoptRequestedBackend: false);

        Assert.AreEqual(AgentBackendIds.Copilot, selected);
    }

    [TestMethod]
    public void FilterThreadsForProject_FiltersProjectThreadsAndCanIncludeInternal()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var project1 = ProjectId.NewVersion7().ToString();
        var project2 = ProjectId.NewVersion7().ToString();

        WorkThreadDescriptor[] threads =
        [
            new WorkThreadDescriptor
            {
                ThreadId = "global",
                Kind = WorkThreadKind.GlobalThread,
                BackendId = "codex",
                BackendSessionId = "session-global",
                WorkingDirectory = @"C:\Users\alexa\.codealta",
                Title = "Global",
                Status = WorkThreadStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp,
            },
            new WorkThreadDescriptor
            {
                ThreadId = "thread-a",
                Kind = WorkThreadKind.ProjectThread,
                BackendId = "codex",
                BackendSessionId = "session-a",
                ProjectRef = project1,
                WorkingDirectory = @"C:\code\project1",
                Title = "Project 1",
                Status = WorkThreadStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp.AddMinutes(1),
            },
            new WorkThreadDescriptor
            {
                ThreadId = "thread-b",
                Kind = WorkThreadKind.InternalThread,
                BackendId = "codex",
                BackendSessionId = "session-b",
                ProjectRef = project1,
                ParentThreadId = "thread-a",
                WorkingDirectory = @"C:\Users\alexa\.codealta\threads\internal\child",
                Title = "Internal child",
                Status = WorkThreadStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp.AddMinutes(2),
            },
            new WorkThreadDescriptor
            {
                ThreadId = "thread-c",
                Kind = WorkThreadKind.ProjectThread,
                BackendId = "copilot",
                BackendSessionId = "session-c",
                ProjectRef = project2,
                WorkingDirectory = @"C:\code\project2",
                Title = "Project 2",
                Status = WorkThreadStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp.AddMinutes(3),
            },
        ];

        var filteredWithoutInternal = CodeAltaApp.FilterThreadsForProject(threads, project1, includeInternal: false);
        var filteredWithInternal = CodeAltaApp.FilterThreadsForProject(threads, project1, includeInternal: true);

        CollectionAssert.AreEqual(new[] { "thread-a" }, filteredWithoutInternal.Select(static thread => thread.ThreadId).ToArray());
        CollectionAssert.AreEqual(new[] { "thread-b", "thread-a" }, filteredWithInternal.Select(static thread => thread.ThreadId).ToArray());
    }

    [TestMethod]
    public void BuildThreadScopeSummary_UsesProjectDisplayNameAndKind()
    {
        var projectId = ProjectId.NewVersion7().ToString();
        ProjectDescriptor[] projects =
        [
            new ProjectDescriptor
            {
                Id = projectId,
                Slug = "codealta",
                DisplayName = "CodeAlta",
                ProjectPath = @"C:\code\CodeAlta",
            },
        ];

        var globalSummary = CodeAltaApp.BuildThreadScopeSummary(
            new WorkThreadDescriptor
            {
                ThreadId = "global",
                Kind = WorkThreadKind.GlobalThread,
                BackendId = "codex",
                BackendSessionId = "session-global",
                WorkingDirectory = @"C:\Users\alexa\.codealta",
                Title = "Global",
                Status = WorkThreadStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow,
            },
            projects,
            @"C:\Users\alexa\.codealta");

        var projectSummary = CodeAltaApp.BuildThreadScopeSummary(
            new WorkThreadDescriptor
            {
                ThreadId = "project",
                Kind = WorkThreadKind.ProjectThread,
                BackendId = "codex",
                BackendSessionId = "session-project",
                ProjectRef = projectId,
                WorkingDirectory = @"C:\code\CodeAlta",
                Title = "Project",
                Status = WorkThreadStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow,
            },
            projects,
            @"C:\Users\alexa\.codealta");

        var internalSummary = CodeAltaApp.BuildThreadScopeSummary(
            new WorkThreadDescriptor
            {
                ThreadId = "internal",
                Kind = WorkThreadKind.InternalThread,
                BackendId = "codex",
                BackendSessionId = "session-internal",
                ProjectRef = projectId,
                WorkingDirectory = @"C:\Users\alexa\.codealta\threads\internal",
                Title = "Internal",
                Status = WorkThreadStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow,
            },
            projects,
            @"C:\Users\alexa\.codealta");

        Assert.AreEqual(@"Global thread · C:\Users\alexa\.codealta", globalSummary);
        Assert.AreEqual(@"CodeAlta · C:\code\CodeAlta", projectSummary);
        Assert.AreEqual("Internal · CodeAlta", internalSummary);
    }

    [TestMethod]
    public void ResolveSidebarThreadAccent_UsesCopilotAccentForCopilotThreads()
    {
        var accent = CodeAltaApp.ResolveSidebarThreadAccent(AgentBackendIds.Copilot.Value, WorkThreadKind.ProjectThread);

        Assert.AreEqual(CodeAltaApp.SidebarAccent.CopilotThread, accent);
    }

    [TestMethod]
    public void ResolveSidebarThreadAccent_UsesKindAccentForCodexThreads()
    {
        var accent = CodeAltaApp.ResolveSidebarThreadAccent(AgentBackendIds.Codex.Value, WorkThreadKind.ProjectThread);

        Assert.AreEqual(CodeAltaApp.SidebarAccent.ProjectThread, accent);
    }

    [TestMethod]
    public void ScrollToTailIfFollowing_DoesNotThrowForDocumentFlow()
    {
        var flow = new DocumentFlow();

        FlowScrollExtensions.ScrollToTailIfFollowing(flow);

        Assert.IsNotNull(flow);
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var tickMethod = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(tickMethod);
        tickMethod.Invoke(app, [null]);
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, null);
    }

}

