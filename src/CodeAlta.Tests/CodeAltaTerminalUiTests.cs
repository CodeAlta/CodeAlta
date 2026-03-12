using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaTerminalUiTests
{
    [TestMethod]
    public void CreatePendingChatMessage_CreatesStreamingMarkdownSynchronously()
    {
        var pending = CodeAltaTerminalUi.CreatePendingChatMessage("hello, who are you?");

        Assert.IsInstanceOfType<MarkdownControl>(pending.StreamingMarkdown);
        Assert.AreEqual(string.Empty, pending.StreamingMarkdown.Markdown);
    }

    [TestMethod]
    public async Task CreatePendingChatMessage_CanBeCreatedFromWorkerThread()
    {
        var pending = await Task.Run(() => CodeAltaTerminalUi.CreatePendingChatMessage("hello"));

        Assert.IsInstanceOfType<MarkdownControl>(pending.StreamingMarkdown);
        Assert.AreEqual(string.Empty, pending.StreamingMarkdown.Markdown);
    }

    [TestMethod]
    public void FormatChatContentMarkdown_PrefixesReasoningContent()
    {
        var markdown = CodeAltaTerminalUi.FormatChatContentMarkdown(AgentContentKind.Reasoning, "Inspecting the project.");

        Assert.AreEqual("Inspecting the project.", markdown);
    }

    [TestMethod]
    public void FormatChatPlanMarkdown_RendersExplanationAndStatuses()
    {
        var markdown = CodeAltaTerminalUi.FormatChatPlanMarkdown(
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
        var markdown = CodeAltaTerminalUi.FormatChatActivityMarkdown(
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
    public void FormatChatSessionUpdateMarkdown_ReturnsMessageOnly()
    {
        var markdown = CodeAltaTerminalUi.FormatChatSessionUpdateMarkdown(
            new AgentSessionUpdateEvent(
                AgentBackendIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.UsageUpdated,
                "13116/128000 tokens"));

        Assert.AreEqual("13116/128000 tokens", markdown);
        StringAssert.Contains(CodeAltaTerminalUi.GetSessionUpdateHeader(AgentSessionUpdateKind.UsageUpdated), "Usage Updated");
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

        var globalHeader = CodeAltaTerminalUi.BuildHeaderText(
            thread: null,
            selectedProject: null,
            globalRoot: @"C:\Users\alexa\.codealta",
            preferredBackendId: AgentBackendIds.Codex.Value,
            globalScopeSelected: true);

        var projectHeader = CodeAltaTerminalUi.BuildHeaderText(
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
        Assert.AreEqual("Send the first prompt to start a global thread.", CodeAltaTerminalUi.BuildDraftPromptMessage(globalScopeSelected: true));
        Assert.AreEqual("Send the first prompt to start a thread for the selected project.", CodeAltaTerminalUi.BuildDraftPromptMessage(globalScopeSelected: false));
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

        Assert.AreEqual("Prompt ready · Global", CodeAltaTerminalUi.BuildReadyStatusText(null, null, globalScopeSelected: true));
        Assert.AreEqual("Prompt ready · CodeAlta", CodeAltaTerminalUi.BuildReadyStatusText(null, project, globalScopeSelected: false));
        Assert.AreEqual("Prompt ready · Review startup", CodeAltaTerminalUi.BuildReadyStatusText(thread, project, globalScopeSelected: false));
    }

    [TestMethod]
    public void BuildStatusIconMarkup_ReturnsColoredIconsPerTone()
    {
        StringAssert.Contains(CodeAltaTerminalUi.BuildStatusIconMarkup(CodeAltaTerminalUi.StatusTone.Ready), "green");
        StringAssert.Contains(CodeAltaTerminalUi.BuildStatusIconMarkup(CodeAltaTerminalUi.StatusTone.Warning), "gold");
        StringAssert.Contains(CodeAltaTerminalUi.BuildStatusIconMarkup(CodeAltaTerminalUi.StatusTone.Error), "red");
        StringAssert.Contains(CodeAltaTerminalUi.BuildStatusIconMarkup(CodeAltaTerminalUi.StatusTone.Info), "deepskyblue");
    }

    [TestMethod]
    public void FormatChatCardTimestamp_UsesInvariantReadableFormat()
    {
        var timestamp = new DateTimeOffset(2026, 03, 12, 14, 5, 6, TimeSpan.FromHours(1));

        var text = CodeAltaTerminalUi.FormatChatCardTimestamp(timestamp);

        Assert.AreEqual("2026-03-12 14:05:06 +01:00", text);
    }

    [TestMethod]
    public async Task ApplyChatCardTimestamp_CanBeCalledFromWorkerThread()
    {
        var timestamp = new DateTimeOffset(2026, 03, 12, 14, 5, 6, TimeSpan.FromHours(1));
        var markup = new Markup();

        await Task.Run(() => CodeAltaTerminalUi.ApplyChatCardTimestamp(markup, timestamp));

        Assert.AreEqual("[dim]2026-03-12 14:05:06 +01:00[/]", markup.Text);
    }

    [TestMethod]
    public void ShouldRunInlineOnCurrentThread_AllowsBootstrapThreadBeforeTerminalStarts()
    {
        Assert.IsTrue(CodeAltaTerminalUi.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: false,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaTerminalUi.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: true,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaTerminalUi.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: true,
            terminalLoopStarted: true));

        Assert.IsFalse(CodeAltaTerminalUi.ShouldRunInlineOnCurrentThread(
            dispatcherHasAccess: false,
            terminalLoopStarted: true));
    }

    [TestMethod]
    public void CompactSidebarThreadTitle_TrimsLongTitlesToSingleLineLength()
    {
        var compact = CodeAltaTerminalUi.CompactSidebarThreadTitle("The lunet-build action in this repository is used like this:");

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

        var tooltip = CodeAltaTerminalUi.BuildThreadSidebarTooltip(thread);

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

        var selection = CodeAltaTerminalUi.ResolveInitialSelection(
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
        var markdown = CodeAltaTerminalUi.FormatChatRawEventMarkdown(
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
    public void IsChatAutoApproveBinding_OnlyMatchesAutoApproveStateValueBinding()
    {
        var autoApproveState = new State<bool>(true);
        var otherState = new State<bool>(false);

        Assert.IsTrue(CodeAltaTerminalUi.IsChatAutoApproveBinding((Binding)autoApproveState, autoApproveState));
        Assert.IsFalse(CodeAltaTerminalUi.IsChatAutoApproveBinding((Binding)otherState, autoApproveState));
    }

    [TestMethod]
    public void FormatChatPermissionRequestMarkdown_RendersTypedAndGenericDetails()
    {
        var typedMarkdown = CodeAltaTerminalUi.FormatChatPermissionRequestMarkdown(
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
        var genericMarkdown = CodeAltaTerminalUi.FormatChatPermissionRequestMarkdown(
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
    public void FormatChatUserInputRequestMarkdown_ShowsChoicesAndImplementationGap()
    {
        var markdown = CodeAltaTerminalUi.FormatChatUserInputRequestMarkdown(
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

        StringAssert.Contains(markdown, "Terminal question prompts are not implemented yet");
        StringAssert.Contains(markdown, "Which option do you prefer?");
        StringAssert.Contains(markdown, "Search first");
        StringAssert.Contains(markdown, "Answer directly");
        StringAssert.Contains(markdown, "Terminal question prompts are not implemented yet");
        StringAssert.Contains(markdown, "Freeform: disabled");
    }

    [TestMethod]
    public void FormatChatUserInputRequestMarkdown_WhenAutoApproveEnabled_DescribesAutoAnswering()
    {
        var markdown = CodeAltaTerminalUi.FormatChatUserInputRequestMarkdown(
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
            autoApprove: true);

        StringAssert.Contains(markdown, "Auto-Approve will prefer continue/inspect-style choices");
    }

    [TestMethod]
    public void CreateChatUserInputResponse_WhenAutoApproveEnabled_SelectsDefaultAnswers()
    {
        var response = CodeAltaTerminalUi.CreateChatUserInputResponse(
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
        var response = CodeAltaTerminalUi.CreateChatUserInputResponse(
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
        var response = CodeAltaTerminalUi.CreateChatUserInputResponse(
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
        var markdown = CodeAltaTerminalUi.FormatChatInteractionResolutionMarkdown(
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
        var markdown = CodeAltaTerminalUi.FormatChatImmediatePermissionDecisionMarkdown(
            new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce),
            autoApprove: true);

        StringAssert.Contains(markdown, "CodeAlta response: auto-approved");
        StringAssert.Contains(markdown, "Decision: Allow Once");
    }

    [TestMethod]
    public void FormatChatImmediateUserInputResponseMarkdown_ShowsReturnedAnswer()
    {
        var markdown = CodeAltaTerminalUi.FormatChatImmediateUserInputResponseMarkdown(
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
        var markdown = CodeAltaTerminalUi.FormatChatInteractionResolutionMarkdown(
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
    public void BuildChatReasoningOptions_PreservesDefaultAndSupportedEfforts()
    {
        var options = CodeAltaTerminalUi.BuildChatReasoningOptions(
            new AgentModelInfo(
                "model-a",
                SupportedReasoningEfforts: [AgentReasoningEffort.Minimal, AgentReasoningEffort.High]));

        Assert.AreEqual("Default", options[0].Label);
        CollectionAssert.AreEqual(
            new[] { "Default", "Minimal", "High" },
            options.Select(static option => option.Label).ToArray());
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

        var markup = CodeAltaTerminalUi.BuildChatBackendStatusMarkup(states, AgentBackendIds.Codex, isInitializing: false);

        StringAssert.Contains(markup, "Codex");
        StringAssert.Contains(markup, "Copilot");
        Assert.IsFalse(markup.Contains("CLI not found", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolveChatBackendSelection_CanPreserveCurrentSelection()
    {
        var selected = CodeAltaTerminalUi.ResolveChatBackendSelection(
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

        var filteredWithoutInternal = CodeAltaTerminalUi.FilterThreadsForProject(threads, project1, includeInternal: false);
        var filteredWithInternal = CodeAltaTerminalUi.FilterThreadsForProject(threads, project1, includeInternal: true);

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

        var globalSummary = CodeAltaTerminalUi.BuildThreadScopeSummary(
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

        var projectSummary = CodeAltaTerminalUi.BuildThreadScopeSummary(
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

        var internalSummary = CodeAltaTerminalUi.BuildThreadScopeSummary(
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
}

