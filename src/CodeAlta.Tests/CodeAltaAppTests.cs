using CodeAlta.Presentation.Controls;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Threading;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Sidebar;
using CodeAlta.Presentation.Styling;
using CodeAlta.Presentation.Sessions;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Presentation.Usage;
using CodeAlta.Views;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Figlet;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaAppTests
{
    [TestMethod]
    public void CodeAltaThemeResolver_UsesNamedPredefinedSchemeOrDefault()
    {
        var selected = CodeAltaThemeResolver.Resolve("Elderberry Dark Soft");
        var fallback = CodeAltaThemeResolver.Resolve("missing scheme");

        Assert.AreEqual(ColorScheme.ElderberryDarkSoft.Name, selected.Scheme?.Name);
        Assert.AreEqual(ColorScheme.RootLoopsDark.Name, fallback.Scheme?.Name);
        Assert.IsNotNull(fallback.Surface);
        Assert.IsNotNull(fallback.InputFill);
    }

    [TestMethod]
    public void CodeAltaThemeResolver_DerivesDirectionalOpaqueSurfaces()
    {
        var light = CodeAltaThemeResolver.Resolve(ColorScheme.RootLoopsLight.Name);
        var dark = CodeAltaThemeResolver.Resolve(ColorScheme.RootLoopsDark.Name);

        Assert.IsNotNull(light.Background);
        Assert.IsNotNull(light.Surface);
        Assert.IsNotNull(light.InputFill);
        Assert.IsNotNull(dark.Background);
        Assert.IsNotNull(dark.Surface);
        Assert.IsNotNull(dark.InputFill);
        Assert.IsTrue(light.Surface.Value.GetRelativeLuminance() < light.Background.Value.GetRelativeLuminance());
        Assert.IsTrue(light.InputFill.Value.GetRelativeLuminance() < light.Background.Value.GetRelativeLuminance());
        Assert.IsTrue(dark.Surface.Value.GetRelativeLuminance() > dark.Background.Value.GetRelativeLuminance());
        Assert.IsTrue(dark.InputFill.Value.GetRelativeLuminance() > dark.Background.Value.GetRelativeLuminance());
    }

    [TestMethod]
    public void CodeAltaThemeResolver_HidesTerminalAndRootLoopsSchemesFromSelectableList()
    {
        var selectableSchemeNames = CodeAltaThemeResolver.GetSelectableSchemes()
            .Select(static scheme => scheme.Name)
            .ToArray();

        Assert.IsFalse(selectableSchemeNames.Contains(ColorScheme.Terminal.Name, StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(selectableSchemeNames.Contains(ColorScheme.RootLoopsDark.Name, StringComparer.OrdinalIgnoreCase));
        Assert.IsFalse(selectableSchemeNames.Contains(ColorScheme.RootLoopsLight.Name, StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(selectableSchemeNames.Contains(ColorScheme.ElderberryDarkSoft.Name, StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual(ColorScheme.Terminal.Name, CodeAltaThemeResolver.Resolve(ColorScheme.Terminal.Name).Scheme?.Name);
    }

    [TestMethod]
    public void UiPalette_UsesThemeSelectionStyleForSidebarSelectedRows()
    {
        var theme = CodeAltaThemeResolver.Resolve(ColorScheme.ElderberryDarkSoft.Name);
        var treeStyle = UiPalette.GetSidebarTreeStyle(theme);

        Assert.IsNotNull(treeStyle.SelectedFocused);
        Assert.IsNotNull(treeStyle.SelectedUnfocused);
        Assert.IsTrue(treeStyle.SelectedFocused.Value.TryGetBackground(out var focusedBackground));
        Assert.IsTrue(treeStyle.SelectedUnfocused.Value.TryGetBackground(out var unfocusedBackground));
        Assert.AreEqual(theme.Selection, focusedBackground);
        Assert.AreEqual(theme.Selection, unfocusedBackground);
    }

    [TestMethod]
    public void UiPalette_DerivesProjectFileIconColorsFromThemeAndCategory()
    {
        var darkTheme = CodeAltaThemeResolver.Resolve(ColorScheme.ElderberryDarkSoft.Name);
        var lightTheme = CodeAltaThemeResolver.Resolve(ColorScheme.BlueberryLight.Name);

        var darkDirectory = UiPalette.GetProjectFileIconColor(darkTheme, "directory", Color.Default);
        var darkCSharp = UiPalette.GetProjectFileIconColor(darkTheme, "csharp", Color.Default);
        var darkTypeScript = UiPalette.GetProjectFileIconColor(darkTheme, "typescript", Color.Default);
        var lightDirectory = UiPalette.GetProjectFileIconColor(lightTheme, "directory", Color.Default);

        Assert.AreNotEqual(Color.Default, darkDirectory);
        Assert.AreNotEqual(Color.Default, darkCSharp);
        Assert.AreNotEqual(Color.Default, darkTypeScript);
        Assert.AreNotEqual(darkDirectory, darkCSharp);
        Assert.AreNotEqual(darkCSharp, darkTypeScript);
        Assert.AreNotEqual(darkDirectory, lightDirectory);
    }

    [TestMethod]
    public void UiPalette_ProjectFileIconColorKeepsConfiguredForeground()
    {
        var theme = CodeAltaThemeResolver.Resolve(ColorScheme.ElderberryDarkSoft.Name);

        var color = UiPalette.GetProjectFileIconColor(theme, "directory", Colors.Red);

        Assert.AreEqual(Colors.Red, color);
    }

    [TestMethod]
    public void UiPalette_UsesVisibleOpaqueTimelineBackgroundsForLightThemes()
    {
        var lightTheme = CodeAltaThemeResolver.Resolve(ColorScheme.BlueberryLight.Name);
        var userBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(lightTheme, ChatTimelineTone.User));
        var assistantBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(lightTheme, ChatTimelineTone.Assistant));
        var reasoningBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(lightTheme, ChatTimelineTone.Reasoning));
        var noticeBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(lightTheme, ChatTimelineTone.Notice));
        var warningBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(lightTheme, ChatTimelineTone.Interaction));
        var fileChangeBackground = GetGroupBackground(UiPalette.GetToolCallGroupStyle(lightTheme));
        var themeBackground = lightTheme.Background.GetValueOrDefault().ToRgb();

        Assert.AreEqual(ColorKind.Rgb, userBackground.Kind);
        Assert.AreEqual(ColorKind.Rgb, assistantBackground.Kind);
        Assert.AreEqual(ColorKind.Rgb, reasoningBackground.Kind);
        Assert.AreEqual(ColorKind.Rgb, noticeBackground.Kind);
        Assert.AreEqual(ColorKind.Rgb, fileChangeBackground.Kind);
        Assert.AreNotEqual(userBackground, reasoningBackground);
        Assert.AreNotEqual(userBackground, noticeBackground);
        var assistantReasoningDistance = GetRgbDistance(assistantBackground, reasoningBackground);
        var reasoningWarningDistance = GetRgbDistance(reasoningBackground, warningBackground);
        Assert.IsTrue(assistantReasoningDistance >= 0.020d, $"Assistant/reasoning light background distance was {assistantReasoningDistance}.");
        Assert.IsTrue(assistantReasoningDistance < reasoningWarningDistance, $"Assistant/reasoning light background distance was {assistantReasoningDistance}; reasoning/warning was {reasoningWarningDistance}.");
        Assert.IsTrue(GetRgbDistance(themeBackground, userBackground) >= 0.06d);
        Assert.IsTrue(GetRgbDistance(themeBackground, fileChangeBackground) >= 0.06d);
    }

    [TestMethod]
    public void UiPalette_KeepsAssistantAndReasoningDistinctForDarkThemes()
    {
        var darkTheme = CodeAltaThemeResolver.Resolve(ColorScheme.ElderberryDarkSoft.Name);
        var assistantBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(darkTheme, ChatTimelineTone.Assistant));
        var reasoningBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(darkTheme, ChatTimelineTone.Reasoning));
        var warningBackground = GetGroupBackground(UiPalette.GetChatGroupStyle(darkTheme, ChatTimelineTone.Interaction));
        var assistantReasoningDistance = GetRgbDistance(assistantBackground, reasoningBackground);
        var reasoningWarningDistance = GetRgbDistance(reasoningBackground, warningBackground);

        Assert.AreEqual(ColorKind.RgbA, assistantBackground.Kind);
        Assert.AreEqual(ColorKind.RgbA, reasoningBackground.Kind);
        Assert.IsTrue(assistantReasoningDistance >= 0.020d, $"Assistant/reasoning dark background distance was {assistantReasoningDistance}.");
        Assert.IsTrue(assistantReasoningDistance < reasoningWarningDistance, $"Assistant/reasoning dark background distance was {assistantReasoningDistance}; reasoning/warning was {reasoningWarningDistance}.");
    }

    [TestMethod]
    public void CreatePendingChatMessage_CreatesStreamingMarkdownSynchronously()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello, who are you?");

        Assert.IsInstanceOfType<MarkdownControl>(pending.StreamingMarkdown);
        Assert.AreEqual(string.Empty, pending.StreamingMarkdown.Markdown);
        Assert.AreEqual(Align.Start, pending.StreamingMarkdown.VerticalAlignment);
    }

    [TestMethod]
    public async Task CreatePendingChatMessage_CanBeCreatedFromWorkerSession()
    {
        var pending = await Task.Run(() => ChatTimelineVisualFactory.CreatePendingChatMessage("hello"));

        Assert.IsInstanceOfType<MarkdownControl>(pending.StreamingMarkdown);
        Assert.AreEqual(string.Empty, pending.StreamingMarkdown.Markdown);
    }

    [TestMethod]
    public void CopyButton_CopiesLatestStreamingMarkdown()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");
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
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");

        var firstItems = ChatTimelineVisualFactory.BuildUserPromptTimelineItems(pending.UserItem, hasSeenUserPrompt: false);
        var laterItems = ChatTimelineVisualFactory.BuildUserPromptTimelineItems(pending.UserItem, hasSeenUserPrompt: true);

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
    public void CreateCollapsibleMarkdownItem_RendersMultipleCollapsibleSections()
    {
        var entry = ChatTimelineVisualFactory.CreateCollapsibleMarkdownItem(
            "summary",
            [
                new ChatCollapsibleMarkdownSection("First details", "first"),
                new ChatCollapsibleMarkdownSection("Second details", "second"),
            ],
            ChatTimelineTone.Notice);

        var document = Assert.IsInstanceOfType<FlowDocument>(entry.Item.Content);
        Assert.AreEqual(1, document.BlockCount);
        var block = Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(block.CreateVisual());
        var stack = Assert.IsInstanceOfType<VStack>(group.Content);

        Assert.AreEqual(0, stack.Spacing);
        Assert.AreEqual(3, stack.Children.Count);
        Assert.IsInstanceOfType<MarkdownControl>(stack.Children[0]);
        Assert.IsInstanceOfType<Collapsible>(stack.Children[1]);
        Assert.IsInstanceOfType<Collapsible>(stack.Children[2]);
    }

    [TestMethod]
    public void CreateCollapsibleMarkdownItem_UsesCustomHeaderVisual()
    {
        var entry = ChatTimelineVisualFactory.CreateCollapsibleMarkdownItem(
            "summary",
            [
                new ChatCollapsibleMarkdownSection(
                    "Detailed statistics",
                    "details",
                    HeaderVisualFactory: () => new Markup("[bold]Turn statistics[/] · compact") { Wrap = false }),
            ],
            ChatTimelineTone.Notice);

        var document = Assert.IsInstanceOfType<FlowDocument>(entry.Item.Content);
        Assert.AreEqual(1, document.BlockCount);
        var block = Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(block.CreateVisual());
        var stack = Assert.IsInstanceOfType<VStack>(group.Content);
        var collapsible = Assert.IsInstanceOfType<Collapsible>(stack.Children[1]);
        var header = Assert.IsInstanceOfType<Markup>(collapsible.Header);

        Assert.IsFalse(header.Wrap);
        Assert.AreEqual("[bold]Turn statistics[/] · compact", header.Text);
    }

    [TestMethod]
    public void CreateVisualItem_RendersCustomVisualAsCardContentAndKeepsCopyMarkdown()
    {
        var entry = ChatTimelineVisualFactory.CreateVisualItem(
            "**Turn statistics** · compact summary",
            () => new Collapsible(
                new Markup("[bold]Turn statistics[/] · compact summary") { Wrap = false },
                new WrapHStack(new TextBlock("details"))),
            ChatTimelineTone.Notice,
            copyDetailSections: [new ChatCollapsibleMarkdownSection("Detailed statistics", "details")]);

        var document = Assert.IsInstanceOfType<FlowDocument>(entry.Item.Content);
        Assert.AreEqual(1, document.BlockCount);
        var block = Assert.IsInstanceOfType<VisualDocumentFlowBlock>(document.GetBlock(0));
        var group = Assert.IsInstanceOfType<Group>(block.CreateVisual());
        var collapsible = Assert.IsInstanceOfType<Collapsible>(group.Content);
        var header = Assert.IsInstanceOfType<Markup>(collapsible.Header);

        Assert.AreEqual("[bold]Turn statistics[/] · compact summary", header.Text);
        Assert.AreEqual("**Turn statistics** · compact summary", entry.Markdown.Markdown);
        Assert.AreEqual(
            "**Turn statistics** · compact summary\r\n\r\n## Detailed statistics\r\n\r\ndetails".Replace("\r\n", Environment.NewLine, StringComparison.Ordinal),
            ChatTimelineVisualFactory.BuildCopyMarkdown(entry.Markdown.Markdown ?? string.Empty, entry.CopyState.DetailSections));
    }

    [TestMethod]
    public void BuildCopyMarkdown_IncludesCollapsibleSections()
    {
        var markdown = ChatTimelineVisualFactory.BuildCopyMarkdown(
            "System Prompt changed: `hash`\n- Tokens: 42",
            [
                new ChatCollapsibleMarkdownSection("Verbatim prompt", "<!-- SystemMessage -->\nsystem"),
                new ChatCollapsibleMarkdownSection("Prompt diff", "```diff\n-old\n+new\n```"),
            ]);

        StringAssert.Contains(markdown, "System Prompt changed: `hash`");
        StringAssert.Contains(markdown, "## Verbatim prompt");
        StringAssert.Contains(markdown, "<!-- SystemMessage -->\nsystem");
        StringAssert.Contains(markdown, "## Prompt diff");
        StringAssert.Contains(markdown, "```diff\n-old\n+new\n```");
    }

    [TestMethod]
    public void FormatSystemPromptSummaryMarkdown_IncludesAgentPromptUsage()
    {
        var promptEvent = new AgentSystemPromptEvent(
            new ModelProviderId("local"),
            "session-1",
            DateTimeOffset.Parse("2026-04-28T12:34:56+00:00"),
            new AgentRunId("run-1"),
            "session_start",
            "781BB1A413420FBED84FE5F685ECB8931819D7D8B343740715D961AC39C084B2",
            "default",
            "System text",
            "Developer text",
            new AgentSystemPromptProviderPayloadSummary("native-system-and-developer", true, false),
            null,
            new AgentSystemPromptStatistics(1414, 3397, 4811, 5656, 13588),
            new AgentSystemPromptChangeSummary("initial", ["system"], [], []),
            new AgentPromptUsageInfo("default", "Default", @"C:\code\CodeAlta\src\CodeAlta.Orchestration\content\prompts\agents\default.prompt.md"));

        var markdown = ChatMarkdownFormatter.FormatSystemPromptSummaryMarkdown(promptEvent);

        StringAssert.Contains(markdown, "System Prompt recorded: `781BB1A413420FBED84FE5F685ECB8931819D7D8B343740715D961AC39C084B2`");
        StringAssert.Contains(markdown, "- Mapping: native-system-and-developer");
        StringAssert.Contains(markdown, @"- Agent Prompt: default (Default - `C:\code\CodeAlta\src\CodeAlta.Orchestration\content\prompts\agents\default.prompt.md`)");
        StringAssert.Contains(markdown, "- Tokens: 4811 approx total (`system` 1414, `developer` 3397)");
    }

    [TestMethod]
    public async Task BuildUserPromptTimelineItems_CanBeCalledFromWorkerSession()
    {
        var pending = ChatTimelineVisualFactory.CreatePendingChatMessage("hello");

        var items = await Task.Run(() => ChatTimelineVisualFactory.BuildUserPromptTimelineItems(pending.UserItem, hasSeenUserPrompt: true));

        Assert.AreEqual(2, items.Count);
        Assert.IsInstanceOfType<FlowDocument>(items[0].Content);
    }

    [TestMethod]
    public void FormatChatContentMarkdown_PrefixesReasoningContent()
    {
        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(AgentContentKind.Reasoning, "Inspecting the project.");

        Assert.AreEqual("Inspecting the project.", markdown);
    }

    [TestMethod]
    public void ReasoningContent_PromotesHeadingIntoHeaderAndTrimsBody()
    {
        const string content = "**Planning tool utilization**\n\nI should inspect the repository structure first.";

        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(AgentContentKind.Reasoning, content);
        var headerSecondary = ChatMarkdownFormatter.GetChatContentHeaderSecondary(AgentContentKind.Reasoning, content);

        Assert.AreEqual("I should inspect the repository structure first.", markdown);
        Assert.AreEqual("Planning tool utilization", headerSecondary);
    }

    [TestMethod]
    public void ReasoningContent_KeepsHeadingVisibleWhenNoBodyExists()
    {
        const string content = "**Planning tool utilization**";

        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(AgentContentKind.Reasoning, content);
        var headerSecondary = ChatMarkdownFormatter.GetChatContentHeaderSecondary(AgentContentKind.Reasoning, content);

        Assert.AreEqual("**Planning tool utilization**", markdown);
        Assert.AreEqual("Planning tool utilization", headerSecondary);
    }

    [TestMethod]
    public void FormatChatPlanMarkdown_RendersExplanationAndStatuses()
    {
        var markdown = ChatMarkdownFormatter.FormatChatPlanMarkdown(
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
        var markdown = ChatMarkdownFormatter.FormatChatActivityMarkdown(
            new AgentActivityEvent(
                ModelProviderIds.Codex,
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
        var markdown = ChatMarkdownFormatter.FormatChatActivityMarkdown(
            new AgentActivityEvent(
                ModelProviderIds.Copilot,
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

        var markdown = ChatMarkdownFormatter.FormatChatActivityMarkdown(
            new AgentActivityEvent(
                ModelProviderIds.Codex,
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
        var markdown = ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(
            new AgentSessionUpdateEvent(
                ModelProviderIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.UsageUpdated,
                "13116/128000 tokens"));

        Assert.AreEqual("13116/128000 tokens", markdown);
        StringAssert.Contains(ChatMarkdownFormatter.GetSessionUpdateHeader(AgentSessionUpdateKind.UsageUpdated), "Usage Updated");
        StringAssert.Contains(ChatMarkdownFormatter.GetSessionUpdateHeader(AgentSessionUpdateKind.ModelChanged), "Model Used");
    }

    [TestMethod]
    public void FormatChatSessionUpdateMarkdown_RebuildsModelUsedMessageFromDetails()
    {
        var update = new AgentSessionUpdateEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.ModelChanged,
            "stale model message",
            JsonSerializer.SerializeToElement(new { providerKey = "codex", modelId = "gpt-5.5", reasoningEffort = "High" }));

        var markdown = ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(update);

        Assert.AreEqual("Model used: provider `codex`, model `gpt-5.5`, reasoning: `High`.", markdown);
    }

    [TestMethod]
    public void FormatChatSessionUpdateMarkdown_UsesLegacyModelUsedMessageWithoutStructuredKeys()
    {
        var update = new AgentSessionUpdateEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.ModelChanged,
            "Model used: provider `codex`, model `gpt-5.5` (reasoning: `High`).",
            JsonSerializer.SerializeToElement(new { modelId = "gpt-5.5", reasoningEffort = "High" }));

        var markdown = ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(update);

        Assert.AreEqual("Model used: provider `codex`, model `gpt-5.5` (reasoning: `High`).", markdown);
    }

    [TestMethod]
    public void BuildDraftPromptMessage_ReflectsSelectedScope()
    {
        Assert.AreEqual("Send the first prompt to start a global session.", ShellTextFormatter.BuildDraftPromptMessage(globalScopeSelected: true));
        Assert.AreEqual("Send the first prompt to start a session for the selected project.", ShellTextFormatter.BuildDraftPromptMessage(globalScopeSelected: false));
    }

    [TestMethod]
    public void BuildWelcomeSubtitle_ReflectsCurrentScope()
    {
        var project = new ProjectDescriptor
        {
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\code\CodeAlta",
        };

        Assert.AreEqual("Global workspace ready for a new session.", ShellTextFormatter.BuildWelcomeSubtitle(null, globalScopeSelected: true));
        Assert.AreEqual("Project draft selected. Choose a project or start typing below.", ShellTextFormatter.BuildWelcomeSubtitle(null, globalScopeSelected: false));
        Assert.AreEqual(@"Next session will start in CodeAlta from folder C:\code\CodeAlta.", ShellTextFormatter.BuildWelcomeSubtitle(project, globalScopeSelected: false));
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
                "Use the prompt below to start a new global session.",
                "Pick a project in the sidebar before sending if you want repository context.",
                "Reopen any session tab to continue previous work.",
            },
            ShellTextFormatter.BuildWelcomeGuidanceLines(null, globalScopeSelected: true).ToArray());

        CollectionAssert.AreEqual(
            new[]
            {
                "Use the prompt below to start a new session for CodeAlta.",
                "Switch projects in the sidebar before sending if you want a different scope.",
                "Reopen any session tab to continue previous work.",
            },
            ShellTextFormatter.BuildWelcomeGuidanceLines(project, globalScopeSelected: false).ToArray());
    }

    [TestMethod]
    public void GetWelcomeFigletFont_LoadsEmbeddedAssetAndCachesInstance()
    {
        var first = WelcomePaneFactory.GetWelcomeFigletFont();
        var second = WelcomePaneFactory.GetWelcomeFigletFont();
        var lines = first.RenderLines("CodeAlta", new FigletRenderOptions { LetterSpacing = 1, TrimTrailingSpaces = true });

        Assert.AreSame(first, second);
        Assert.IsTrue(lines.Length > 0);
        Assert.IsTrue(lines.Any(static line => !string.IsNullOrWhiteSpace(line)));
    }

    [TestMethod]
    public void BuildWelcomeAltaGradientStops_UseMatchingEdgeColors()
    {
        var stops = UiPalette.BuildWelcomeAltaGradientStops(Theme.Default);
        var lightStops = UiPalette.BuildWelcomeAltaGradientStops(Theme.DefaultLight);

        Assert.AreEqual(11, stops.Length);
        Assert.AreEqual(0.00f, stops[0].Offset);
        Assert.AreEqual(0.50f, stops[5].Offset);
        Assert.AreEqual(1.00f, stops[^1].Offset);
        Assert.AreEqual(stops[0].Color, stops[^1].Color);
        Assert.AreEqual(stops[2].Color, stops[8].Color);
        CollectionAssert.AreEqual(stops, lightStops);
    }

    [TestMethod]
    public void ComputeLoopAnimationPhase_RepeatsAcrossCycleBoundary()
    {
        const long cycleTicks = TimeSpan.TicksPerSecond * 5L;

        var start = ShellAnimationRuntime.ComputeLoopAnimationPhase(0, cycleTicks);
        var midpoint = ShellAnimationRuntime.ComputeLoopAnimationPhase(cycleTicks / 2, cycleTicks);
        var end = ShellAnimationRuntime.ComputeLoopAnimationPhase(cycleTicks, cycleTicks);

        Assert.AreEqual(0f, start, 0.0001f);
        Assert.AreEqual(0.5f, midpoint, 0.0001f);
        Assert.AreEqual(start, end, 0.0001f);
    }

    [TestMethod]
    public void ShellAnimationRuntime_Advance_UpdatesLoopingAnimationStates()
    {
        var runtime = new ShellAnimationRuntime();

        runtime.Advance();

        Assert.IsTrue(runtime.WelcomePhase01.Value is >= 0f and < 1f);
        Assert.IsTrue(runtime.ThinkingPhase01.Value is >= 0f and < 1f);
    }

    [TestMethod]
    public void BuildThinkingGradientStops_UseMultipleHighlightsAndLoopableEdges()
    {
        var stops = StatusVisualFormatter.BuildThinkingGradientStops(Theme.Default);

        Assert.AreEqual(9, stops.Length);
        Assert.AreEqual(0.00f, stops[0].Offset);
        Assert.AreEqual(1.00f, stops[^1].Offset);
        Assert.AreEqual(stops[0].Color, stops[^1].Color);
    }

    [TestMethod]
    public void CreateInitialSessionHistoryLoadPlan_StartsAtLastUserPrompt()
    {
        var timestamp = DateTimeOffset.UtcNow;
        AgentEvent[] history =
        [
            new AgentContentDeltaEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.Assistant, "assistant-1", null, "First reply"),
            new AgentContentDeltaEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.Assistant, "assistant-2", null, "Second reply"),
        ];

        var plan = SessionHistoryCoordinator.CreateInitialLoadPlan(history);

        Assert.AreEqual(3, plan.EventsToRender.Count);
        Assert.AreSame(history[3], plan.EventsToRender[0]);
        Assert.AreEqual(2, plan.OmittedMessageCount);
    }

    [TestMethod]
    public void CreateInitialSessionHistoryLoadPlan_PinsLatestAuditEventsBeforeLastUserPrompt()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var systemPrompt = CreateSystemPromptEvent(timestamp.AddSeconds(3), "sha256:latest");
        var modelChanged = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp.AddSeconds(2),
            null,
            AgentSessionUpdateKind.ModelChanged,
            "Model changed to gpt-5.");
        AgentEvent[] history =
        [
            CreateSystemPromptEvent(timestamp, "sha256:old"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.Assistant, "assistant-1", null, "First reply"),
            modelChanged,
            systemPrompt,
            new AgentContentDeltaEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp, null, AgentContentKind.Assistant, "assistant-2", null, "Second reply"),
        ];

        var plan = SessionHistoryCoordinator.CreateInitialLoadPlan(history);

        Assert.AreEqual(5, plan.EventsToRender.Count);
        Assert.AreSame(modelChanged, plan.EventsToRender[0]);
        Assert.AreSame(systemPrompt, plan.EventsToRender[1]);
        Assert.AreSame(history[5], plan.EventsToRender[2]);
        Assert.AreEqual(3, plan.OmittedMessageCount);
    }

    [TestMethod]
    public void RecoverUsageFromHistory_UsesUsageEventsOmittedFromInitialRenderWindow()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(203_533, 272_000, 42, "Active context window"),
            LastOperation: new AgentOperationUsageSnapshot(
                Model: "gpt-5.5",
                InputTokens: 676,
                OutputTokens: 105,
                CachedInputTokens: 202_752),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: timestamp.AddSeconds(2));
        AgentEvent[] history =
        [
            new AgentContentCompletedEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp, null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(1), null, AgentContentKind.Assistant, "assistant-1", null, "First reply"),
            new AgentSessionUpdateEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(2), null, AgentSessionUpdateKind.UsageUpdated, "Usage updated.", Usage: usage),
            new AgentContentCompletedEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(3), null, AgentContentKind.User, "user-2", null, "Second prompt"),
        ];

        var plan = SessionHistoryCoordinator.CreateInitialLoadPlan(history);
        var recovered = SessionHistoryCoordinator.RecoverUsageFromHistory(history);

        Assert.IsFalse(plan.EventsToRender.OfType<AgentSessionUpdateEvent>().Any(static @event => @event.Usage is not null));
        Assert.IsNotNull(recovered);
        Assert.AreEqual(203_533L, recovered.CurrentTokens);
        Assert.AreEqual(272_000L, recovered.TokenLimit);
        Assert.AreEqual(202_752L, recovered.LastOperation?.CachedInputTokens);
    }

    [TestMethod]
    public void RecoverModelProviderPreferenceFromHistory_UsesLatestModelChangedEvent()
    {
        var timestamp = DateTimeOffset.UtcNow;
        AgentEvent[] history =
        [
            new AgentSessionUpdateEvent(
                ModelProviderIds.OpenAIResponses,
                "session-1",
                timestamp,
                null,
                AgentSessionUpdateKind.ModelChanged,
                "Model selected: `gpt-5`.",
                JsonSerializer.SerializeToElement(new { modelId = "gpt-5", reasoningEffort = "Medium" })),
            new AgentContentCompletedEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(1), null, AgentContentKind.User, "user-1", null, "Prompt"),
            new AgentSessionUpdateEvent(
                ModelProviderIds.OpenAIResponses,
                "session-1",
                timestamp.AddSeconds(2),
                null,
                AgentSessionUpdateKind.ModelChanged,
                "Model selected: `gpt-5.5`.",
                JsonSerializer.SerializeToElement(new { modelId = "gpt-5.5", reasoningEffort = "High" })),
        ];

        var preference = SessionHistoryCoordinator.RecoverModelProviderPreferenceFromHistory(history);

        Assert.IsNotNull(preference);
        Assert.AreEqual(ModelProviderIds.OpenAIResponses.Value, preference.ModelProviderId.Value);
        Assert.AreEqual("gpt-5.5", preference.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, preference.ReasoningEffort);
    }

    [TestMethod]
    public void RecoverNotesMarkdownFromHistory_UsesLatestSetOrClearEvent()
    {
        var timestamp = DateTimeOffset.UtcNow;
        AgentEvent[] history =
        [
            new AgentNotesEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp, null, AgentNotesUpdateKind.Set, "# First"),
            new AgentContentCompletedEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(1), null, AgentContentKind.User, "user-1", null, "Prompt"),
            new AgentNotesEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(2), null, AgentNotesUpdateKind.Set, "# Latest"),
            new AgentNotesEvent(ModelProviderIds.OpenAIResponses, "session-1", timestamp.AddSeconds(3), null, AgentNotesUpdateKind.Cleared, string.Empty),
        ];

        var recovered = SessionHistoryCoordinator.RecoverNotesMarkdownFromHistory(history);

        Assert.AreEqual(string.Empty, recovered);
    }

    [TestMethod]
    public void FindPriorSystemPromptForFirstRenderedSystemPrompt_SeedsPinnedPromptDiff()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var initialSystemPrompt = CreateSystemPromptEvent(timestamp, "sha256:old");
        var changedSystemPrompt = CreateSystemPromptEvent(timestamp.AddSeconds(3), "sha256:new", changeKind: "changed");
        AgentEvent[] history =
        [
            initialSystemPrompt,
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp.AddSeconds(1), null, AgentContentKind.User, "user-1", null, "First prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp.AddSeconds(2), null, AgentContentKind.Assistant, "assistant-1", null, "First reply"),
            changedSystemPrompt,
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp.AddSeconds(4), null, AgentContentKind.User, "user-2", null, "Second prompt"),
            new AgentContentCompletedEvent(ModelProviderIds.Copilot, "session-1", timestamp.AddSeconds(5), null, AgentContentKind.Assistant, "assistant-2", null, "Second reply"),
        ];
        var plan = SessionHistoryCoordinator.CreateInitialLoadPlan(history);

        var seed = SessionHistoryCoordinator.FindPriorSystemPromptForFirstRenderedSystemPrompt(history, plan.EventsToRender);

        Assert.AreSame(changedSystemPrompt, plan.EventsToRender[0]);
        Assert.AreSame(initialSystemPrompt, seed);
        Assert.IsNull(SessionHistoryCoordinator.FindPriorSystemPromptForFirstRenderedSystemPrompt(history, history));
    }

    [TestMethod]
    public void BuildTruncatedHistoryTexts_UseHelpfulPluralization()
    {
        Assert.AreEqual("Load 1 previous message", ChatTimelineVisualFactory.BuildTruncatedHistoryLoadButtonText(1));
        Assert.AreEqual("Load 12 previous messages", ChatTimelineVisualFactory.BuildTruncatedHistoryLoadButtonText(12));
        Assert.AreEqual("1 previous message...", ChatTimelineVisualFactory.BuildTruncatedHistorySummaryText(1));
        Assert.AreEqual("12 previous messages...", ChatTimelineVisualFactory.BuildTruncatedHistorySummaryText(12));
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
        var action = ChatTimelineVisualFactory.CreateDeferredUiAction(() => invocationCount++);

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
    public void AnchoredPopupView_ShowAndClose_ToggleOpenStateAndContent()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(80, 20)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var anchor = new TextBlock("anchor");
        var app = new TerminalApp(
            anchor,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
                EnableMouse = true,
                MouseMode = TerminalMouseMode.Move,
            });
        var popupView = new AnchoredPopupView(() => new TextBlock("usage"));

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            popupView.Show(anchor);

            Assert.IsTrue(popupView.IsOpen);
            Assert.AreSame(anchor, popupView.Popup.Anchor);
            Assert.IsInstanceOfType<TextBlock>(popupView.Popup.Content);

            popupView.Close();

            Assert.IsFalse(popupView.IsOpen);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void BuildWelcomePane_CreatesCenteredFigletLogo()
    {
        var welcome = WelcomePaneFactory.Build(null, globalScopeSelected: true, new State<float>(0f));

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
            SessionRuntimeEventCoordinator.ShouldPromoteAgentEventToThinking(
                new AgentContentDeltaEvent(
                    ModelProviderIds.Copilot,
                    "session-1",
                    timestamp,
                    null,
                    AgentContentKind.Reasoning,
                    "content-1",
                    null,
                    "Inspecting reconnect state...")));

        Assert.IsTrue(
            SessionRuntimeEventCoordinator.ShouldPromoteAgentEventToThinking(
                new AgentActivityEvent(
                    ModelProviderIds.Copilot,
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
            SessionRuntimeEventCoordinator.ShouldPromoteAgentEventToThinking(
                new AgentSessionUpdateEvent(
                    ModelProviderIds.Copilot,
                    "session-1",
                    timestamp,
                    null,
                    AgentSessionUpdateKind.Idle,
                    "Idle")));

        Assert.IsFalse(
            SessionRuntimeEventCoordinator.ShouldPromoteAgentEventToThinking(
                new AgentErrorEvent(
                    ModelProviderIds.Copilot,
                    "session-1",
                    timestamp,
                    "Reconnect failed.",
                    exception: null)));
    }

    [TestMethod]
    public void ShouldApplyShellChromeProjectionAfterRuntimeEvent_SkipsUsageOnlySessionUpdates()
    {
        var runtimeEvent = new SessionAgentEvent(
            "session-1",
            new AgentSessionUpdateEvent(
                ModelProviderIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.UsageUpdated,
                "usage updated"));

        Assert.IsFalse(SessionRuntimeEventCoordinator.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent));
    }

    [TestMethod]
    public void ShouldApplyShellChromeProjectionAfterRuntimeEvent_KeepsShellRefreshForNonUsageEvents()
    {
        var runtimeEvent = new SessionAgentEvent(
            "session-1",
            new AgentSessionUpdateEvent(
                ModelProviderIds.Codex,
                "session-1",
                DateTimeOffset.UtcNow,
                null,
                AgentSessionUpdateKind.Warning,
                "warning"));

        Assert.IsTrue(SessionRuntimeEventCoordinator.ShouldApplyShellChromeProjectionAfterRuntimeEvent(runtimeEvent));
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

        var session = new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        Assert.AreEqual("Prompt ready", ShellTextFormatter.BuildReadyStatusText(null, null, globalScopeSelected: true));
        Assert.AreEqual("Prompt ready", ShellTextFormatter.BuildReadyStatusText(null, project, globalScopeSelected: false));
        Assert.AreEqual("Prompt ready", ShellTextFormatter.BuildReadyStatusText(session, project, globalScopeSelected: false));
    }

    [TestMethod]
    public void BuildPromptUnavailablePlaceholder_UsesProviderStateAndSelection()
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        Assert.AreEqual(
            "Waiting for Codex to reconnect...",
            PromptComposerProjectionBuilder.BuildPromptUnavailablePlaceholder(session, "Codex", ModelProviderAvailability.Probing, anyProviderReady: false));
        Assert.AreEqual(
            "Configure model providers (Ctrl+G Ctrl+R) to start a session...",
            PromptComposerProjectionBuilder.BuildPromptUnavailablePlaceholder(null, "Codex", ModelProviderAvailability.Unsupported, anyProviderReady: false));
    }

    [TestMethod]
    public void BuildPromptUnavailableStatusText_DescribesConnectingAndMissingProviders()
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        Assert.AreEqual(
            "Reconnecting session 'Review startup' to Codex. Prompt sending is temporarily unavailable.",
            PromptComposerProjectionBuilder.BuildPromptUnavailableStatusText(session, "Codex", ModelProviderAvailability.Probing, anyProviderReady: false));
        Assert.AreEqual(
            "No model provider is ready. Open Model Providers (Ctrl+G Ctrl+R) to configure one.",
            PromptComposerProjectionBuilder.BuildPromptUnavailableStatusText(null, "Codex", ModelProviderAvailability.Unsupported, anyProviderReady: false));
    }

    [TestMethod]
    public void ClassifyProviderInitializationFailure_TreatsMissingExecutableAsUnsupported()
    {
        var providerState = new ModelProviderState(ModelProviderIds.Codex, "Codex");

        var result = ModelProviderInitializationCoordinator.ClassifyFailure(
            providerState,
            new FileNotFoundException("codex executable was not found"));

        Assert.AreEqual(ModelProviderAvailability.Unsupported, result.Availability);
        StringAssert.Contains(result.StatusMessage, "Codex is unavailable");
    }

    [TestMethod]
    public void BuildStatusIconMarkup_ReturnsColoredIconsPerTone()
    {
        StringAssert.Contains(StatusVisualFormatter.BuildStatusIconMarkup(StatusTone.Ready), TerminalIcons.MdCheckCircleOutline.ToString());
        StringAssert.Contains(StatusVisualFormatter.BuildStatusIconMarkup(StatusTone.Warning), TerminalIcons.MdAlertOutline.ToString());
        StringAssert.Contains(StatusVisualFormatter.BuildStatusIconMarkup(StatusTone.Error), TerminalIcons.MdAlertCircleOutline.ToString());
        StringAssert.Contains(StatusVisualFormatter.BuildStatusIconMarkup(StatusTone.Info), TerminalIcons.OctInfo.ToString());
    }

    [TestMethod]
    public void CanLoadSessionHistory_AllowsRecoveredActiveSessionsWithoutStartedAt()
    {
        var recoverableSession = new SessionViewDescriptor
        {
            SessionId = "copilot:session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Copilot.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Recovered Session",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
            StartedAt = null,
        };

        Assert.IsTrue(SessionHistoryCoordinator.CanLoadSessionHistory(recoverableSession));
    }

    [TestMethod]
    public void TryInferCopilotToolName_InfersReadAndGlobFromCompletionPayload()
    {
        using var globJson = JsonDocument.Parse(
            """
            {
              "result": {
                "content": "C:\\code\\Tomlyn\\readme.md\nC:\\code\\Tomlyn\\src\\Tomlyn\\Tomlyn.csproj"
              }
            }
            """);
        var globResult = ToolCallEventInterpreter.TryInferCopilotToolName(globJson.RootElement.Clone(), out var globName);

        Assert.IsTrue(globResult);
        Assert.AreEqual("glob", globName);

        using var readJson = JsonDocument.Parse(
            """
            {
              "result": {
                "content": "1. <Project>\n2.   <PropertyGroup>\n3.   </PropertyGroup>"
              }
            }
            """);
        var readResult = ToolCallEventInterpreter.TryInferCopilotToolName(readJson.RootElement.Clone(), out var readName);

        Assert.IsTrue(readResult);
        Assert.AreEqual("read", readName);
    }

    [TestMethod]
    public void PreferToolDisplayName_KeepsExplicitCopilotStartNameWhenCompletionIsGeneric()
    {
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
            ModelProviderIds.Copilot,
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

        var chosen = ToolCallEventInterpreter.PreferToolDisplayName("view", "glob", completion);

        Assert.AreEqual("view", chosen);
    }

    [TestMethod]
    public void ResolveToolArgumentText_FormatsCopilotArgumentsObject()
    {
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
            ModelProviderIds.Copilot,
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

        var arguments = ToolCallEventInterpreter.ResolveToolArgumentText(activity);

        Assert.IsNotNull(arguments);
        StringAssert.Contains(arguments, "\"path\": \"C:\\\\code\\\\Tomlyn\"");
        StringAssert.Contains(arguments, "\"pattern\": \"**/*.csproj\"");
    }

    [TestMethod]
    public void ResolveToolCommandText_UsesStructuredCodexFunctionArguments()
    {
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
            ModelProviderIds.Codex,
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

        var command = ToolCallEventInterpreter.ResolveToolCommandText(activity);

        Assert.AreEqual(@"Get-Content C:\code\Tomlyn\readme.md -TotalCount 250", command);
    }

    [TestMethod]
    public void ResolveToolDisplayName_UsesCodexShellCommandVerb()
    {
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
            ModelProviderIds.Codex,
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

        var displayName = ToolCallEventInterpreter.ResolveToolDisplayName(activity);

        Assert.AreEqual("Get-Content", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_PreservesMeaningfulDotnetSubcommand()
    {
        var displayName = ToolCallEventInterpreter.ExtractCommandDisplayName("dotnet test CodeAlta.Tests/CodeAlta.Tests.csproj -c Release");

        Assert.AreEqual("dotnet test", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_PreservesMeaningfulGitSubcommand()
    {
        var displayName = ToolCallEventInterpreter.ExtractCommandDisplayName("git status --short");

        Assert.AreEqual("git status", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_SkipsFlagsForSecondLevelGitCommand()
    {
        var displayName = ToolCallEventInterpreter.ExtractCommandDisplayName("git remote -v");

        Assert.AreEqual("git remote", displayName);
    }

    [TestMethod]
    public void ExtractCommandDisplayName_DoesNotTreatFlagsOrPathsAsSubcommands()
    {
        var listDisplayName = ToolCallEventInterpreter.ExtractCommandDisplayName("ls -al");
        var copyDisplayName = ToolCallEventInterpreter.ExtractCommandDisplayName("cp /mnt/c/source.txt /mnt/c/dest.txt");

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
    public void BuildToolCallSummaryMarkup_ShowsDiffStatsForModifyingTools()
    {
        var entry = new ToolCallEntryState(
            "call-1",
            new Button(new TextBlock("tool")),
            new Markup("tool"))
        {
            ActivityKind = AgentActivityKind.ToolCall,
            Status = ToolCallDisplayStatus.Completed,
            DisplayName = "apply_patch",
            DiffText =
                """
                diff --git a/src/App.cs b/src/App.cs
                --- a/src/App.cs
                +++ b/src/App.cs
                @@ -1,2 +1,2 @@
                 unchanged
                -old
                +new
                """,
        };
        entry.OutputBuffer.AppendLine("Patch applied");

        var markup = ToolCallSummaryFormatter.BuildSummaryMarkup(entry);
        var markdown = ToolCallSummaryFormatter.BuildDetailMarkdown(entry);

        StringAssert.Contains(markup, "+1");
        StringAssert.Contains(markup, "-1");
        StringAssert.Contains(markup, "apply_patch");
        StringAssert.Contains(markdown, "- Changes: `+1 -1`");
    }

    [TestMethod]
    public void FormatSystemPromptDiffMarkdown_BuildsContextualUnifiedDiff()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var previousSystemLines = Enumerable.Range(1, 12)
            .Select(static line => line == 8 ? "old rule" : $"system line {line:00}");
        var currentSystemLines = Enumerable.Range(1, 12)
            .Select(static line => line == 8 ? "new rule" : $"system line {line:00}");
        var previous = CreateSystemPromptEvent(
            timestamp,
            "sha256:old",
            systemMessage: string.Join('\n', previousSystemLines),
            developerInstructions: "developer\nkeep",
            changeKind: "initial");
        var current = CreateSystemPromptEvent(
            timestamp.AddSeconds(1),
            "sha256:new",
            systemMessage: string.Join('\n', currentSystemLines),
            developerInstructions: "developer\nkeep",
            changeKind: "changed");

        var markdown = ChatMarkdownFormatter.FormatSystemPromptDiffMarkdown(previous, current);

        StringAssert.Contains(markdown, "```diff");
        StringAssert.Contains(markdown, "--- system-prompt/sha256:old");
        StringAssert.Contains(markdown, "+++ system-prompt/sha256:new");
        StringAssert.Contains(markdown, "-old rule");
        StringAssert.Contains(markdown, "+new rule");
        StringAssert.Contains(markdown, "@@ -6,7 +6,7 @@");
        Assert.IsFalse(markdown.Contains("system line 01", StringComparison.Ordinal));
        Assert.IsFalse(markdown.Contains("<!-- DeveloperInstructions -->", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatSystemPromptDiffMarkdown_ReturnsEmptyForIdenticalPrompt()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var previous = CreateSystemPromptEvent(timestamp, "sha256:old");
        var current = CreateSystemPromptEvent(timestamp.AddSeconds(1), "sha256:new", changeKind: "changed");

        var markdown = ChatMarkdownFormatter.FormatSystemPromptDiffMarkdown(previous, current);

        Assert.AreEqual(string.Empty, markdown);
    }

    [TestMethod]
    public void FormatChatContentMarkdown_SanitizesInlineImagePayloads()
    {
        var markdown = ChatMarkdownFormatter.FormatChatContentMarkdown(
            AgentContentKind.User,
            $"Please inspect this.{Environment.NewLine}<image>{Environment.NewLine}data:image/png;base64,AAAA");

        Assert.AreEqual($"Please inspect this.{Environment.NewLine}Inline Image", markdown);
        Assert.IsFalse(markdown.Contains("data:image", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FormatChatCardTimestamp_UsesInvariantReadableFormat()
    {
        var timestamp = new DateTimeOffset(2026, 03, 12, 14, 5, 6, TimeSpan.FromHours(1));
        var expected = timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var text = ChatTimelineVisualFactory.FormatTimestamp(timestamp);

        Assert.AreEqual(expected, text);
    }

    [TestMethod]
    public async Task ApplyChatCardTimestamp_CanBeCalledFromWorkerSession()
    {
        var timestamp = new DateTimeOffset(2026, 03, 12, 14, 5, 6, TimeSpan.FromHours(1));
        var expected = $"[dim]{timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}[/]";
        var markup = new Markup();

        await Task.Run(() => ChatTimelineVisualFactory.ApplyTimestamp(markup, timestamp));

        Assert.AreEqual(expected, markup.Text);
    }

    [TestMethod]
    public void ShouldRunInlineOnCurrentSession_AllowsBootstrapSessionBeforeTerminalStarts()
    {
        Assert.IsTrue(CodeAltaApp.ShouldRunInlineOnCurrentSession(
            dispatcherHasAccess: false,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaApp.ShouldRunInlineOnCurrentSession(
            dispatcherHasAccess: true,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaApp.ShouldRunInlineOnCurrentSession(
            dispatcherHasAccess: true,
            terminalLoopStarted: true));

        Assert.IsFalse(CodeAltaApp.ShouldRunInlineOnCurrentSession(
            dispatcherHasAccess: false,
            terminalLoopStarted: true));
    }

    [TestMethod]
    public void CanAccessBindableState_RequiresUiSessionAfterTerminalStarts()
    {
        Assert.IsTrue(CodeAltaApp.CanAccessBindableState(
            dispatcherHasAccess: false,
            terminalLoopStarted: false));
        Assert.IsTrue(CodeAltaApp.CanAccessBindableState(
            dispatcherHasAccess: true,
            terminalLoopStarted: false));
        Assert.IsTrue(CodeAltaApp.CanAccessBindableState(
            dispatcherHasAccess: true,
            terminalLoopStarted: true));
        Assert.IsFalse(CodeAltaApp.CanAccessBindableState(
            dispatcherHasAccess: false,
            terminalLoopStarted: true));
    }

    [TestMethod]
    public void ShouldRunDeferredUiActionInlineOnCurrentSession_OnlyAllowsBootstrapSession()
    {
        Assert.IsFalse(CodeAltaApp.ShouldRunDeferredUiActionInlineOnCurrentSession(
            dispatcherHasAccess: false,
            terminalLoopStarted: false));

        Assert.IsTrue(CodeAltaApp.ShouldRunDeferredUiActionInlineOnCurrentSession(
            dispatcherHasAccess: true,
            terminalLoopStarted: false));

        Assert.IsFalse(CodeAltaApp.ShouldRunDeferredUiActionInlineOnCurrentSession(
            dispatcherHasAccess: true,
            terminalLoopStarted: true));

        Assert.IsFalse(CodeAltaApp.ShouldRunDeferredUiActionInlineOnCurrentSession(
            dispatcherHasAccess: false,
            terminalLoopStarted: true));
    }

    [TestMethod]
    public void SidebarSessionTitle_PreservesFullTitle()
    {
        const string title = "  The lunet-build action in this repository is used like this:  ";

        var row = new SidebarNodeViewModel("session:test", SidebarNodeKind.Session, selectionTarget: null);
        row.UpdateTitle(title);

        Assert.AreEqual(title, row.Title);
    }

    [TestMethod]
    public void ResolveInitialSelection_DefersSelectedSessionRestoreUntilUiLoopStarts()
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Investigate startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        var selection = InitialSessionSelectionResolver.Resolve(
            new SessionViewViewState
            {
                OpenSessionIds = ["session-1"],
                Selection = SessionViewSelectionState.Session("session-1", "project-1"),
                SelectedSessionId = "session-1",
            },
            [session]);

        Assert.AreEqual("session-1", selection.SelectedSessionId);
        Assert.AreEqual("session-1", selection.StartupSessionRestoreId);
    }

    [TestMethod]
    public void FormatChatRawEventMarkdown_RendersBackendEventTypeAndPayload()
    {
        using var payloadJson = JsonDocument.Parse("""{"kind":"shell","toolCallId":"call-1"}""");
        var markdown = ChatMarkdownFormatter.FormatChatRawEventMarkdown(
            new AgentRawEvent(
                ModelProviderIds.Copilot,
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
            ModelProviderIds.Codex,
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
            ModelProviderIds.Codex,
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
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.ToolCall,
            AgentActivityPhase.Completed,
            "tool-1",
            null,
            "view",
            "Done");

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayActivity(turn));
        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayActivity(toolStart));
        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayActivity(toolCompleted));
    }

    [TestMethod]
    public void ShouldDisplayActivity_HidesToolGroupedSubagentLifecycleAndKeepsCompaction()
    {
        var subagent = new AgentActivityEvent(
            ModelProviderIds.Codex,
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
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentActivityKind.Compaction,
            AgentActivityPhase.Completed,
            "compaction-1",
            null,
            null,
            "Compaction completed.");

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayActivity(subagent));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplayActivity(compaction));
    }

    [TestMethod]
    public void ShouldDisplayRawEvent_HidesRawEventsFromTimeline()
    {
        using var payloadJson = JsonDocument.Parse("""{"kind":"shell"}""");
        var raw = new AgentRawEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            "tool.started",
            payloadJson.RootElement.Clone());

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayRawEvent(raw));
    }

    [TestMethod]
    public void ShouldDisplaySessionUpdate_ShowsWarningsModelChangesAndCompactionLifecycle()
    {
        var copilotUsage = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.UsageUpdated,
            "token usage");
        var copilotResumed = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.Resumed,
            "session resumed");
        var copilotWarning = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.Warning,
            "warning");
        var copilotModelChanged = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.ModelChanged,
            "Model changed to gpt-5.");
        var copilotCompactionStarted = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.CompactionStarted,
            "Compaction started.");
        var copilotCompactionCompleted = new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.CompactionCompleted,
            "Compaction completed.");
        var codexUsage = new AgentSessionUpdateEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.UsageUpdated,
            "token usage");

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(copilotUsage));
        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(copilotResumed));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(copilotWarning));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(copilotModelChanged));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(copilotCompactionStarted));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(copilotCompactionCompleted));
        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplaySessionUpdate(codexUsage));
    }

    [TestMethod]
    public void FormatChatSessionUpdateMarkdown_LocalCompactionDetails_IncludesStatisticsAndSummary()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "schema": "codealta.localCompaction.v1",
              "summaryMarkdown": "## Objective\nContinue the task.",
              "tokensBefore": 12000,
              "tokensAfter": 4500,
              "tokensRemoved": 7500,
              "compressionRatio": 0.375,
              "targetRatio": 0.10,
              "targetTokens": 3000,
              "targetMet": false,
              "targetMissReason": "latest_user_anchor",
              "planningAttemptCount": 3,
              "postCompactionInputRatio": 0.15,
              "summarizedMessageCount": 8,
              "keptMessageCount": 3,
              "messagesAfter": 4,
              "summaryPromptInputTokens": 2400,
              "summaryPromptIncludedMessageCount": 10,
              "summaryPromptTotalMessageCount": 11,
              "summaryCallCount": 2,
              "summaryMaxOutputTokens": 1024,
              "chunkCount": 2,
              "totalToolCallCount": 5,
              "serializedToolCallCount": 4,
              "collapsedToolCallCount": 1,
              "totalToolResultCount": 5,
              "serializedToolResultCount": 4,
              "serializedToolResultExcerptCount": 3,
              "omittedToolResultCount": 2,
              "serializedToolResultCharacters": 900,
              "totalReasoningCount": 2,
              "serializedReasoningCount": 1,
              "omittedReasoningCount": 1,
              "serializedReasoningCharacters": 300,
              "omittedAttachmentCount": 1,
              "droppedMessageCount": 1,
              "readFiles": ["src/A.cs", "src/B.cs"],
              "modifiedFiles": ["src/C.cs"]
            }
            """);
        var update = new AgentSessionUpdateEvent(
            ModelProviderIds.OpenAIResponses,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.CompactionCompleted,
            "Manual local compaction summarized 8 messages.",
            Details: document.RootElement.Clone());

        var markdown = ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(update);

        StringAssert.Contains(markdown, "**Efficiency**");
        StringAssert.Contains(markdown, "Context: 12,000 → 4,500 tokens");
        StringAssert.Contains(markdown, "Target: 3,000 tokens (10.0 % of input limit), actual 15.0 % of input limit, missed (latest user anchor exceeded target), 3 planning attempts");
        StringAssert.Contains(markdown, "Summarizer: 2 calls, 2 chunks");
        StringAssert.Contains(markdown, "Tool outputs: 3/5 with excerpts");
        StringAssert.Contains(markdown, "1 modified files and 2 read files tracked");
        Assert.IsTrue(ChatMarkdownFormatter.TryGetCompactionSummaryMarkdown(update, out var summaryMarkdown));
        Assert.AreEqual("## Objective\nContinue the task.", summaryMarkdown);
    }

    [TestMethod]
    public void FormatChatSessionUpdateMarkdown_LocalCompactionDetails_OldMetadataWithoutTargetRenders()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "schema": "codealta.localCompaction.v1",
              "summaryMarkdown": "## Objective\nContinue the task.",
              "tokensBefore": 1000,
              "tokensAfter": 200,
              "summarizedMessageCount": 2,
              "keptMessageCount": 1,
              "messagesAfter": 2
            }
            """);
        var update = new AgentSessionUpdateEvent(
            ModelProviderIds.OpenAIResponses,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentSessionUpdateKind.CompactionCompleted,
            "Manual local compaction summarized 2 messages.",
            Details: document.RootElement.Clone());

        var markdown = ChatMarkdownFormatter.FormatChatSessionUpdateMarkdown(update);

        StringAssert.Contains(markdown, "Context: 1,000 → 200 tokens");
        StringAssert.Contains(markdown, "Messages: summarized 2, kept 1, after 2");
        Assert.IsFalse(markdown.Contains("Target:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShouldDisplayPermissionRequest_HidesAutoApprovedPermissions()
    {
        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled: true));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled: false));
    }

    [TestMethod]
    public void ShouldDisplayInteraction_HidesAutoApprovedPermissionResolutions()
    {
        var permissionResolved = new AgentInteractionEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentInteractionKind.PermissionResolved,
            "interaction-1",
            "Permission resolved.");
        var userInputResolved = new AgentInteractionEvent(
            ModelProviderIds.Copilot,
            "session-1",
            DateTimeOffset.UtcNow,
            null,
            AgentInteractionKind.UserInputResolved,
            "interaction-2",
            "Input resolved.");

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayInteraction(permissionResolved, autoApproveEnabled: true));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplayInteraction(permissionResolved, autoApproveEnabled: false));
        Assert.IsTrue(ChatMarkdownFormatter.ShouldDisplayInteraction(userInputResolved, autoApproveEnabled: true));
    }

    [TestMethod]
    public void FormatChatPermissionRequestMarkdown_RendersTypedAndGenericDetails()
    {
        var typedMarkdown = ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(
            new AgentCommandPermissionRequest(
                ModelProviderIds.Codex,
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
        var genericMarkdown = ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(
            new AgentGenericPermissionRequest(
                ModelProviderIds.Copilot,
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
        var markdown = ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(
            new AgentUserInputRequest(
                ModelProviderIds.Copilot,
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
                    ])),
            autoApprove: true);

        StringAssert.Contains(markdown, "CodeAlta will prefer continue/inspect-style choices");
        StringAssert.Contains(markdown, "Which option do you prefer?");
        StringAssert.Contains(markdown, "Search first");
        StringAssert.Contains(markdown, "Answer directly");
        StringAssert.Contains(markdown, "Freeform: disabled");
    }

    [TestMethod]
    public void FormatChatUserInputRequestMarkdown_WhenAutoApproveDisabled_DescribesImplementationGap()
    {
        var markdown = ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(
            new AgentUserInputRequest(
                ModelProviderIds.Copilot,
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
        var response = ChatPromptResponseBuilder.CreateResponse(
            new AgentUserInputRequest(
                ModelProviderIds.Copilot,
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
        var response = ChatPromptResponseBuilder.CreateResponse(
            new AgentUserInputRequest(
                ModelProviderIds.Copilot,
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
        var response = ChatPromptResponseBuilder.CreateResponse(
            new AgentUserInputRequest(
                ModelProviderIds.Copilot,
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
        var markdown = ChatMarkdownFormatter.FormatChatInteractionResolutionMarkdown(
            new AgentInteractionEvent(
                ModelProviderIds.Copilot,
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
        var markdown = ChatMarkdownFormatter.FormatChatImmediatePermissionDecisionMarkdown(
            new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce),
            autoApprove: true);

        StringAssert.Contains(markdown, "CodeAlta response: auto-approved");
        StringAssert.Contains(markdown, "Decision: Allow Once");
    }

    [TestMethod]
    public void FormatChatImmediatePermissionDecisionMarkdown_ShowsInteractiveDecision()
    {
        var markdown = ChatMarkdownFormatter.FormatChatImmediatePermissionDecisionMarkdown(
            new AgentPermissionDecision(AgentPermissionDecisionKind.AllowForSession),
            autoApprove: false);

        StringAssert.Contains(markdown, "CodeAlta response: approved this request for the session.");
        StringAssert.Contains(markdown, "Decision: Allow For Session");
    }

    [TestMethod]
    public void FormatChatImmediateUserInputResponseMarkdown_ShowsReturnedAnswer()
    {
        var markdown = ChatMarkdownFormatter.FormatChatImmediateUserInputResponseMarkdown(
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
        var markdown = ChatMarkdownFormatter.FormatChatInteractionResolutionMarkdown(
            new AgentInteractionEvent(
                ModelProviderIds.Copilot,
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
        var options = ModelProviderPresentation.BuildReasoningOptions(
            new AgentModelInfo(
                "model-a",
                SupportedReasoningEfforts: [AgentReasoningEffort.Minimal, AgentReasoningEffort.High]));

        CollectionAssert.AreEqual(
            new[] { "Minimal", "High" },
            options.Select(static option => option.Label).ToArray());
    }

    [TestMethod]
    public void BuildChatReasoningOptions_UsesNoneWhenModelDoesNotSupportReasoning()
    {
        var options = ModelProviderPresentation.BuildReasoningOptions(
            new AgentModelInfo(
                "model-a",
                SupportedReasoningEfforts: []));

        CollectionAssert.AreEqual(
            new[] { "None" },
            options.Select(static option => option.Label).ToArray());
    }

    [TestMethod]
    public void ResolvePreferredReasoningEffort_PrefersHighWhenSupportedAndNoPreferenceIsSet()
    {
        var effort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
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
        var effort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
            new AgentModelInfo(
                "gpt-5.4",
                DefaultReasoningEffort: AgentReasoningEffort.High,
                SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High]),
            preferredReasoningEffort: AgentReasoningEffort.Low);

        Assert.AreEqual(AgentReasoningEffort.Low, effort);
    }

    [TestMethod]
    public void ResolvePreferredReasoningEffort_DropsRequestedEffortWhenModelDoesNotSupportReasoning()
    {
        var effort = ModelProviderPresentation.ResolvePreferredReasoningEffort(
            new AgentModelInfo(
                "gemini-test",
                SupportedReasoningEfforts: []),
            preferredReasoningEffort: AgentReasoningEffort.Low);

        Assert.IsNull(effort);
    }

    [TestMethod]
    public void ResolvePreferredModelId_FallsBackToFirstAvailableModel()
    {
        AgentModelInfo[] models =
        [
            new("gpt-5.4"),
            new("gpt-5-mini"),
        ];

        var selectedModelId = ModelProviderPresentation.ResolvePreferredModelId(models, "missing-model");

        Assert.AreEqual("gpt-5.4", selectedModelId);
    }

    [TestMethod]
    public void BuildModelOptions_PreservesSelectedModelMissingFromCatalog()
    {
        var providerState = new ModelProviderState(new ModelProviderId("codex"), "Codex subscription")
        {
            SelectedModelId = "gpt-5.5",
        };
        providerState.Models.Add(new AgentModelInfo("gpt-5.2", DisplayName: "GPT-5.2"));

        var options = ModelProviderPresentation.BuildModelOptions(providerState);

        Assert.AreEqual("gpt-5.5", options[0].ModelId);
        Assert.AreEqual("gpt-5.5", options[0].Label);
        Assert.AreEqual("gpt-5.2", options[1].ModelId);
    }

    [TestMethod]
    public void BuildModelOptions_FallsBackToIdWhenDisplayNameIsBlank()
    {
        var providerState = new ModelProviderState(new ModelProviderId("qwen"), "Qwen");
        providerState.Models.Add(new AgentModelInfo("qwen3-flash", DisplayName: " "));

        var options = ModelProviderPresentation.BuildModelOptions(providerState);

        Assert.AreEqual("qwen3-flash", options[0].Label);
    }

    [TestMethod]
    public void BuildModelOptionMarkup_IncludesDimModelIdWhenDisplayNameDiffers()
    {
        var markup = ModelProviderPresentation.BuildModelOptionMarkup(new ChatModelOption("qwen3-flash", "Qwen3.6 Flash"));

        Assert.AreEqual("Qwen3.6 Flash [dim](qwen3-flash)[/]", markup);
    }

    [TestMethod]
    public void BuildModelOptionMarkup_OmitsModelIdWhenLabelAlreadyIsId()
    {
        var markup = ModelProviderPresentation.BuildModelOptionMarkup(new ChatModelOption("qwen3-flash", "qwen3-flash"));

        Assert.AreEqual("qwen3-flash", markup);
    }

    [TestMethod]
    public void BuildModelOptions_UsesSelectedModelWhenCatalogIsEmpty()
    {
        var providerState = new ModelProviderState(new ModelProviderId("codex"), "Codex subscription")
        {
            SelectedModelId = "gpt-5.5",
        };

        var options = ModelProviderPresentation.BuildModelOptions(providerState);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("gpt-5.5", options[0].ModelId);
    }

    [TestMethod]
    public void ShouldDisplayCompletedContent_HidesEmptyReasoning()
    {
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.Parse("2026-03-14T14:02:50+00:00"),
            new AgentRunId("turn-1"),
            AgentContentKind.Reasoning,
            "reasoning-1",
            null,
            string.Empty);

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayCompletedContent(completed));
    }

    [TestMethod]
    public void ShouldDisplayCompletedContent_HidesCommandOutput()
    {
        var completed = new AgentContentCompletedEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.Parse("2026-03-14T14:02:50+00:00"),
            new AgentRunId("turn-1"),
            AgentContentKind.CommandOutput,
            "command-1",
            null,
            "Exit code: 0");

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayCompletedContent(completed));
    }

    [TestMethod]
    public void ShouldDisplayContentDelta_HidesCommandOutput()
    {
        var delta = new AgentContentDeltaEvent(
            ModelProviderIds.Codex,
            "session-1",
            DateTimeOffset.Parse("2026-03-14T14:02:50+00:00"),
            new AgentRunId("turn-1"),
            AgentContentKind.CommandOutput,
            "command-1",
            null,
            "partial output");

        Assert.IsFalse(ChatMarkdownFormatter.ShouldDisplayContentDelta(delta));
    }

    [TestMethod]
    public void BuildModelProviderStatusMarkup_IncludesWarningsAndSelectedProvider()
    {
        var states = new[]
        {
            new ModelProviderState(ModelProviderIds.Codex, "Codex")
            {
                Availability = ModelProviderAvailability.Ready,
                StatusMessage = "Connected · 2 models",
            },
            new ModelProviderState(ModelProviderIds.Copilot, "Copilot")
            {
                Availability = ModelProviderAvailability.Unsupported,
                StatusMessage = "Copilot is unavailable: CLI not found.",
            },
        };

        var markup = ModelProviderPresentation.BuildProviderStatusMarkup(states, ModelProviderIds.Codex, isInitializing: false);

        StringAssert.Contains(markup, "Codex");
        StringAssert.Contains(markup, "Copilot");
        Assert.IsFalse(markup.Contains("CLI not found", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildProviderSummaryMarkup_IncludesProviderAndErrorCounts()
    {
        var states = new[]
        {
            new ModelProviderState(ModelProviderIds.Codex, "Codex")
            {
                Availability = ModelProviderAvailability.Ready,
            },
            new ModelProviderState(ModelProviderIds.Copilot, "Copilot")
            {
                Availability = ModelProviderAvailability.Failed,
            },
        };

        var markup = ModelProviderPresentation.BuildProviderSummaryMarkup(states, isInitializing: false);

        StringAssert.Contains(markup, "1 active provider");
        StringAssert.Contains(markup, "2 configured");
        StringAssert.Contains(markup, "1 error");
    }

    [TestMethod]
    public void BuildProviderSummaryMarkup_UsesConfiguredProviderCountWhenProvided()
    {
        var states = new[]
        {
            new ModelProviderState(ModelProviderIds.Codex, "Codex")
            {
                Availability = ModelProviderAvailability.Ready,
            },
            new ModelProviderState(ModelProviderIds.Copilot, "Copilot")
            {
                Availability = ModelProviderAvailability.Failed,
            },
        };

        var markup = ModelProviderPresentation.BuildProviderSummaryMarkup(
            states,
            isInitializing: false,
            configuredProviderCount: 6);

        StringAssert.Contains(markup, "1 active provider");
        StringAssert.Contains(markup, "6 configured");
        StringAssert.Contains(markup, "1 error");
    }

    [TestMethod]
    public void BuildProviderSummaryMarkup_CountsMissingConfiguredProvidersAsErrors()
    {
        var states = new[]
        {
            new ModelProviderState(ModelProviderIds.Codex, "Codex")
            {
                Availability = ModelProviderAvailability.Ready,
            },
            new ModelProviderState(ModelProviderIds.Copilot, "Copilot")
            {
                Availability = ModelProviderAvailability.Failed,
            },
        };

        var markup = ModelProviderPresentation.BuildProviderSummaryMarkup(
            states,
            isInitializing: false,
            configuredProviderKeys: ["codex", "copilot", "openai", "anthropic", "google", "vertex"]);

        StringAssert.Contains(markup, "1 active provider");
        StringAssert.Contains(markup, "6 configured");
        StringAssert.Contains(markup, "5 errors");
    }

    [TestMethod]
    public void ReplaceSelectItems_DoesNotMutateSelectWhenItemsAreUnchanged()
    {
        var select = new Select<ChatModelOption>();
        ModelProviderPresentation.ReplaceSelectItems(
            select,
            [new ChatModelOption("gpt-5.4", "GPT-5.4"), new ChatModelOption(null, "(default)")]);

        var versionBefore = select.Items.Version;

        ModelProviderPresentation.ReplaceSelectItems(
            select,
            [new ChatModelOption("gpt-5.4", "GPT-5.4"), new ChatModelOption(null, "(default)")]);

        Assert.AreEqual(versionBefore, select.Items.Version);
        Assert.AreEqual(2, select.Items.Count);
        CollectionAssert.AreEqual(
            new[] { new ChatModelOption("gpt-5.4", "GPT-5.4"), new ChatModelOption(null, "(default)") },
            select.Items.ToArray());
    }

    [TestMethod]
    public void ReplaceSelectItems_ReplacesSelectItemsWhenContentChanges()
    {
        var select = new Select<ChatReasoningOption>();
        ModelProviderPresentation.ReplaceSelectItems(
            select,
            [new ChatReasoningOption(AgentReasoningEffort.Low, "Low")]);

        var versionBefore = select.Items.Version;

        ModelProviderPresentation.ReplaceSelectItems(
            select,
            [new ChatReasoningOption(AgentReasoningEffort.High, "High")]);

        Assert.AreNotEqual(versionBefore, select.Items.Version);
        Assert.AreEqual(1, select.Items.Count);
        Assert.AreEqual(new ChatReasoningOption(AgentReasoningEffort.High, "High"), select.Items[0]);
    }

    [TestMethod]
    public void SessionUsageFormatting_UsesInvariantCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

            var usage = new AgentSessionUsage(
                Window: new AgentWindowUsageSnapshot(50000, 120000, 12, "Active context window"),
                Scope: AgentUsageScope.CurrentWindow,
                Source: AgentUsageSource.CopilotSessionUsageInfo,
                UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:00:00+00:00"));

            var indicator = SessionUsageAggregator.BuildIndicatorMarkup(usage);
            var summary = SessionUsageAggregator.FormatSummary(usage);

            Assert.AreEqual("[dim]Context[/] [success]42%[/]", indicator);
            Assert.AreEqual("50,000 / 120,000 input tokens (41.7%)", summary);
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
            Source: AgentUsageSource.CodexSessionTokenUsageUpdated,
            UpdatedAt: DateTimeOffset.Parse("2026-03-18T21:48:22+00:00"),
            Details: new CodexSessionUsageDetails(
                LastTurnUsage: new CodexTokenUsage(40064, 40283, 190, 33, 40473),
                TotalUsage: new CodexTokenUsage(32809344, 35409515, 161773, 67166, 35571288),
                ModelContextWindow: 258400));

        var indicator = SessionUsageAggregator.BuildIndicatorMarkup(usage);
        var summary = SessionUsageAggregator.FormatSummary(usage);

        Assert.AreEqual("[dim]Context[/] [success]16%[/]", indicator);
        Assert.AreEqual("40,473 / 258,400 input tokens (15.7%)", summary);
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
            Source: AgentUsageSource.CodexSessionTokenUsageUpdated,
            UpdatedAt: DateTimeOffset.Parse("2026-03-19T05:59:24+00:00"),
            Details: new CodexSessionUsageDetails(
                LastTurnUsage: new CodexTokenUsage(199424, 200435, 86, 14, 200535),
                TotalUsage: new CodexTokenUsage(31249920, 33493301, 148132, 61352, 33641433),
                ModelContextWindow: 258400));

        var indicator = SessionUsageAggregator.BuildIndicatorMarkup(usage);
        var markdown = SessionUsageAggregator.BuildMarkdown(usage, "Codex", "gpt-5.4");

        Assert.AreEqual("[dim]Context[/] [warning]78%[/]", indicator);
        StringAssert.Contains(markdown, "Compaction pressure: 200,535 / 258,400 input tokens (77.6%)");
        StringAssert.Contains(markdown, "Session total: total 33,641,433");
    }

    [TestMethod]
    public void BuildSessionUsageIndicatorMarkup_UsesErrorToneNearWindowLimit()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(95000, 100000, null, "Active context window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CopilotSessionUsageInfo,
            UpdatedAt: DateTimeOffset.Parse("2026-03-19T08:00:00+00:00"));

        var indicator = SessionUsageAggregator.BuildIndicatorMarkup(usage);

        Assert.AreEqual("[dim]Context[/] [error]95%[/]", indicator);
    }

    [TestMethod]
    public void SessionUsageFormatting_CapsOverLimitPressureDisplay()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(3_495_220, 272_000, 63, "Estimated active context"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: DateTimeOffset.Parse("2026-05-18T18:38:43+00:00"));

        var indicator = SessionUsageAggregator.BuildIndicatorMarkup(usage);
        var summary = SessionUsageAggregator.FormatSummary(usage);

        Assert.AreEqual("[dim]Context[/] [error]100%[/]", indicator);
        Assert.AreEqual("≥272,000 / 272,000 input tokens (100%)", summary);
    }

    [TestMethod]
    public void SessionUsageFormatting_ShowsCurrentTokensWhenWindowLimitIsUnknown()
    {
        var usage = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(12450, null, 7, "Estimated active context"),
            LastOperation: new AgentOperationUsageSnapshot(
                InputTokens: 12000,
                OutputTokens: 450,
                Label: "Last API call"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.ProviderUsage,
            UpdatedAt: DateTimeOffset.Parse("2026-04-08T10:00:00+00:00"));

        var indicator = SessionUsageAggregator.BuildIndicatorMarkup(usage);
        var summary = SessionUsageAggregator.FormatSummary(usage);

        Assert.AreEqual("[dim]Context[/] [dim]12.5k tok[/]", indicator);
        Assert.AreEqual("12,450 tokens · 7 messages", summary);
    }

    [TestMethod]
    public void MergeSessionUsage_MergesTypedProviderDetails()
    {
        var current = new AgentSessionUsage(
            Window: new AgentWindowUsageSnapshot(4096, 128000, 8, "Active session window"),
            Scope: AgentUsageScope.CurrentWindow,
            Source: AgentUsageSource.CodexSessionTokenUsageUpdated,
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

        var merged = SessionUsageAggregator.Merge(current, incoming);

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

        var merged = SessionUsageAggregator.Merge(current, incoming);

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
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

            var markdown = SessionUsageAggregator.BuildMarkdown(
                new AgentSessionUsage(
                    Window: new AgentWindowUsageSnapshot(
                        50000,
                        120000,
                        12,
                        "Active context window",
                        TotalContextEnvelope: 400000,
                        MaxOutputTokens: 128000),
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
            StringAssert.Contains(markdown, "## Context usage: 12 messages");
            StringAssert.Contains(markdown, "## Limits");
            StringAssert.Contains(markdown, "## Provider-specific details");
            StringAssert.Contains(markdown, "50,000 / 120,000 input tokens (41.7%)");
            StringAssert.Contains(markdown, "Indicative model limits: context window 400,000 tokens; max output 128,000 tokens");
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
        var markdown = SessionUsageAggregator.BuildMarkdown(
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
        var popupText = SessionUsageAggregator.FormatOperationPopupText(
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
        var popupText = SessionUsageAggregator.FormatOperationPopupText(
            new AgentOperationUsageSnapshot(
                InputTokens: 103252,
                OutputTokens: 234,
                CachedInputTokens: 99968,
                ReasoningTokens: 164,
                Label: "Last turn"));

        Assert.IsNull(popupText);
    }

    [TestMethod]
    public void ResolveModelProviderSelection_CanPreserveCurrentSelection()
    {
        var selected = ModelProviderPresentation.ResolveProviderSelection(
            ModelProviderIds.Copilot,
            ModelProviderIds.Codex,
            adoptRequestedProvider: false);

        Assert.AreEqual(ModelProviderIds.Copilot, selected);
    }

    [TestMethod]
    public void FilterSessionsForProject_FiltersProjectSessionsAndCanIncludeInternal()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var project1 = ProjectId.NewVersion7().ToString();
        var project2 = ProjectId.NewVersion7().ToString();

        SessionViewDescriptor[] sessions =
        [
            new SessionViewDescriptor
            {
                SessionId = "global",
                Kind = SessionViewKind.GlobalSession,
                ProviderId = "codex",
                WorkingDirectory = @"C:\Users\alexa\.alta",
                Title = "Global",
                Status = SessionViewStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp,
            },
            new SessionViewDescriptor
            {
                SessionId = "session-a",
                Kind = SessionViewKind.ProjectSession,
                ProviderId = "codex",
                ProjectRef = project1,
                WorkingDirectory = @"C:\code\project1",
                Title = "Project 1",
                Status = SessionViewStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp.AddMinutes(1),
            },
            new SessionViewDescriptor
            {
                SessionId = "session-b",
                Kind = SessionViewKind.InternalSession,
                ProviderId = "codex",
                ProjectRef = project1,
                ParentSessionId = "session-a",
                WorkingDirectory = @"C:\Users\alexa\.alta\sessions\internal\child",
                Title = "Internal child",
                Status = SessionViewStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp.AddMinutes(2),
            },
            new SessionViewDescriptor
            {
                SessionId = "session-c",
                Kind = SessionViewKind.ProjectSession,
                ProviderId = "copilot",
                ProjectRef = project2,
                WorkingDirectory = @"C:\code\project2",
                Title = "Project 2",
                Status = SessionViewStatus.Active,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                LastActiveAt = timestamp.AddMinutes(3),
            },
        ];

        var filteredWithoutInternal = SessionScopePresentation.FilterSessionsForProject(sessions, project1, includeInternal: false);
        var filteredWithInternal = SessionScopePresentation.FilterSessionsForProject(sessions, project1, includeInternal: true);

        CollectionAssert.AreEqual(new[] { "session-a" }, filteredWithoutInternal.Select(static session => session.SessionId).ToArray());
        CollectionAssert.AreEqual(new[] { "session-b", "session-a" }, filteredWithInternal.Select(static session => session.SessionId).ToArray());
    }

    [TestMethod]
    public void BuildSessionScopeSummary_UsesProjectDisplayNameAndKind()
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

        var globalSummary = SessionScopePresentation.BuildScopeSummary(
            new SessionViewDescriptor
            {
                SessionId = "global",
                Kind = SessionViewKind.GlobalSession,
                ProviderId = "codex",
                WorkingDirectory = @"C:\Users\alexa\.alta",
                Title = "Global",
                Status = SessionViewStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow,
            },
            projects,
            @"C:\Users\alexa\.alta");

        var projectSummary = SessionScopePresentation.BuildScopeSummary(
            new SessionViewDescriptor
            {
                SessionId = "project",
                Kind = SessionViewKind.ProjectSession,
                ProviderId = "codex",
                ProjectRef = projectId,
                WorkingDirectory = @"C:\code\CodeAlta",
                Title = "Project",
                Status = SessionViewStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow,
            },
            projects,
            @"C:\Users\alexa\.alta");

        var internalSummary = SessionScopePresentation.BuildScopeSummary(
            new SessionViewDescriptor
            {
                SessionId = "internal",
                Kind = SessionViewKind.InternalSession,
                ProviderId = "codex",
                ProjectRef = projectId,
                WorkingDirectory = @"C:\Users\alexa\.alta\sessions\internal",
                Title = "Internal",
                Status = SessionViewStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastActiveAt = DateTimeOffset.UtcNow,
            },
            projects,
            @"C:\Users\alexa\.alta");

        Assert.AreEqual(@"Global session · C:\Users\alexa\.alta", globalSummary);
        Assert.AreEqual(@"CodeAlta · C:\code\CodeAlta", projectSummary);
        Assert.AreEqual("Internal · CodeAlta", internalSummary);
    }

    [TestMethod]
    public void ResolveSidebarSessionAccent_UsesKindAccentForCopilotSessions()
    {
        var accent = SidebarSessionPresentation.ResolveSessionAccent(ModelProviderIds.Copilot.Value, SessionViewKind.ProjectSession);

        Assert.AreEqual(SidebarAccent.ProjectSession, accent);
    }

    [TestMethod]
    public void ResolveSidebarSessionAccent_UsesKindAccentForCodexSessions()
    {
        var accent = SidebarSessionPresentation.ResolveSessionAccent(ModelProviderIds.Codex.Value, SessionViewKind.ProjectSession);

        Assert.AreEqual(SidebarAccent.ProjectSession, accent);
    }

    [TestMethod]
    public void BuildSidebarSessionProviderMarkup_UsesSidebarAccentAndProviderLabel()
    {
        var markup = SidebarSessionPresentation.BuildProviderMarkup(ModelProviderIds.Copilot.Value, displayName: null, SessionViewKind.ProjectSession);

        StringAssert.Contains(markup, UiPalette.GetSidebarAccentMarkup(SidebarAccent.ProjectSession));
        StringAssert.Contains(markup, "Copilot");
        StringAssert.Contains(markup, TerminalIcons.MdCircleSmall.ToString());
    }

    [TestMethod]
    public void ResolveProviderDisplayName_PrefersConfiguredDisplayName()
    {
        var displayName = SidebarSessionPresentation.ResolveProviderDisplayName("myresponses", "OpenAI (Responses)");

        Assert.AreEqual("OpenAI (Responses)", displayName);
    }

    [TestMethod]
    public void ResolvePreferredExpandedProjectId_OnlyExpandsSelectedSessionProject()
    {
        Assert.IsNull(SidebarSelectionResolver.ResolvePreferredExpandedProjectId(selectedSessionProjectId: null));
        Assert.IsNull(SidebarSelectionResolver.ResolvePreferredExpandedProjectId(string.Empty));
        Assert.AreEqual("project-1", SidebarSelectionResolver.ResolvePreferredExpandedProjectId("project-1"));
    }

    [TestMethod]
    public void PromptDraftCoordinator_PreservesSeparateDraftsPerSession()
    {
        var coordinator = new PromptDraftCoordinator();
        var first = new SessionState();
        var second = new SessionState();

        coordinator.RememberPrompt(first, "first prompt");
        coordinator.RememberPrompt(second, "second prompt");
        coordinator.RememberPrompt(session: null, "draft prompt");

        Assert.AreEqual("first prompt", coordinator.GetPrompt(first));
        Assert.AreEqual("second prompt", coordinator.GetPrompt(second));
        Assert.AreEqual("draft prompt", coordinator.GetPrompt(session: null));
    }

    [TestMethod]
    public void PromptDraftUiCoordinator_PreservesPromptTextPerSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var selectedSessionId = "session-1";
        var coordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            new CatalogOptions { GlobalRoot = root },
            () => ShellSelection.Session(selectedSessionId, "project-1"),
            new FrontendEventPublisher(new InlineUiDispatcher()));
        try
        {
            var first = new SessionState();
            var second = new SessionState();

            coordinator.SyncPromptText(first);
            coordinator.PromptText = "first prompt";
            selectedSessionId = "session-2";
            coordinator.SyncPromptText(second);
            Assert.AreEqual(string.Empty, coordinator.PromptText);

            coordinator.PromptText = "second prompt";
            coordinator.SyncPromptText(session: null);
            Assert.AreEqual(string.Empty, coordinator.PromptText);

            coordinator.PromptText = "draft prompt";
            selectedSessionId = "session-1";
            coordinator.SyncPromptText(first);
            Assert.AreEqual("first prompt", coordinator.PromptText);

            selectedSessionId = "session-2";
            coordinator.SyncPromptText(second);
            Assert.AreEqual("second prompt", coordinator.PromptText);

            coordinator.SyncPromptText(session: null);
            Assert.AreEqual("draft prompt", coordinator.PromptText);
        }
        finally
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PromptDraftUiCoordinator_PreservesPromptTextPerProjectDraft()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var selectedProjectId = "project-1";
        var coordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            new CatalogOptions { GlobalRoot = root },
            () => ShellSelection.ProjectDraft(selectedProjectId),
            new FrontendEventPublisher(new InlineUiDispatcher()));
        try
        {
            coordinator.SyncPromptText(session: null);
            coordinator.PromptText = "project one prompt";

            selectedProjectId = "project-2";
            coordinator.SyncPromptText(session: null);
            Assert.AreEqual(string.Empty, coordinator.PromptText);
            coordinator.PromptText = "project two prompt";

            selectedProjectId = "project-1";
            coordinator.SyncPromptText(session: null);
            Assert.AreEqual("project one prompt", coordinator.PromptText);

            selectedProjectId = "project-2";
            coordinator.SyncPromptText(session: null);
            Assert.AreEqual("project two prompt", coordinator.PromptText);
        }
        finally
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PromptDraftUiCoordinator_PersistsProjectDraftPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
            var coordinator = new PromptDraftUiCoordinator(
                new PromptDraftCoordinator(),
                new CatalogOptions { GlobalRoot = root },
                static () => ShellSelection.ProjectDraft("project-1"),
                publisher);

            coordinator.SyncPromptText(session: null);
            coordinator.PromptText = "persist project draft";
            await coordinator.DisposeAsync().ConfigureAwait(false);

            var reloaded = new PromptDraftUiCoordinator(
                new PromptDraftCoordinator(),
                new CatalogOptions { GlobalRoot = root },
                static () => ShellSelection.ProjectDraft("project-1"),
                publisher);

            reloaded.SyncPromptText(session: null);
            Assert.AreEqual("persist project draft", reloaded.PromptText);
            await reloaded.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PromptDraftUiCoordinator_ClearDraftPromptTextClearsProjectDraftAfterSessionSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var selection = ShellSelection.ProjectDraft("project-1");
        try
        {
            var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
            var coordinator = new PromptDraftUiCoordinator(
                new PromptDraftCoordinator(),
                new CatalogOptions { GlobalRoot = root },
                () => selection,
                publisher);

            coordinator.SyncPromptText(session: null);
            coordinator.PromptText = "used project draft";
            selection = ShellSelection.Session("session-1", "project-1");

            coordinator.ClearDraftPromptText();

            Assert.IsFalse(coordinator.HasDraftPrompt("project-1", isGlobal: false));
            await coordinator.DisposeAsync().ConfigureAwait(false);

            selection = ShellSelection.ProjectDraft("project-1");
            var reloaded = new PromptDraftUiCoordinator(
                new PromptDraftCoordinator(),
                new CatalogOptions { GlobalRoot = root },
                () => selection,
                publisher);

            reloaded.SyncPromptText(session: null);
            Assert.AreEqual(string.Empty, reloaded.PromptText);
            await reloaded.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PromptDraftUiCoordinator_FirstSessionPromptCharacterPublishesPromptDraftEvent()
    {
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = new PromptDraftUiCoordinator(
            new PromptDraftCoordinator(),
            new CatalogOptions { GlobalRoot = Path.GetTempPath() },
            static () => ShellSelection.Session("session-1", "project-1"),
            publisher);

        coordinator.SyncPromptText(new SessionState());
        coordinator.PromptText = "a";

        Assert.IsFalse(events.OfType<CatalogChangedEvent>().Any());
        Assert.IsTrue(events.OfType<PromptDraftChangedEvent>().Any(e => e.PromptSessionId == "session-1"));
    }

    [TestMethod]
    public void CreateStyledPromptEditor_PreservesMarkdownHighlighting()
    {
        var editor = SessionWorkspaceView.CreateStyledPromptEditor(_ => { }, onOpenHelp: null, onOpenCommandPalette: null, placeholder: "Prompt");
        var style = editor.GetStyle<PromptEditorStyle>();

        Assert.IsFalse(editor.Highlighter.IsEmpty);
        Assert.IsTrue(editor.EnableWordHints);
        Assert.AreEqual(PromptEditorEscapeBehavior.CancelCompletionOnly, editor.EscapeBehavior);
        Assert.AreEqual(PromptEditorEnterMode.EnterAccepts, editor.EnterMode);
        Assert.AreEqual(new Thickness(0, 0, 1, 0), style.Padding);
        Assert.IsNull(style.PromptForeground);
        Assert.IsNull(style.ContinuationPromptForeground);
        Assert.IsNull(style.GhostForeground);
        Assert.IsNull(style.Background);
        Assert.IsNull(style.PromptSidebarBackground);
        Assert.IsNull(style.PromptSeparatorForeground);
        Assert.IsNull(style.PlaceholderForeground);
        Assert.IsNull(style.Selection);
    }

    [TestMethod]
    public void ChatPromptEditor_TransientShortcuts_OpenHelpAndPaletteWithoutTyping()
    {
        var helpCount = 0;
        var paletteCount = 0;
        var editor = new ChatPromptEditor(
            _ => { },
            () => helpCount++,
            () => paletteCount++);

        Assert.IsTrue(editor.TryHandleTransientShortcutInput("?"));
        Assert.AreEqual(1, helpCount);
        Assert.AreEqual(0, paletteCount);
        Assert.IsTrue(string.IsNullOrEmpty(editor.Text));

        Assert.IsTrue(editor.TryHandleTransientShortcutInput("/"));
        Assert.AreEqual(1, helpCount);
        Assert.AreEqual(1, paletteCount);
        Assert.IsTrue(string.IsNullOrEmpty(editor.Text));
    }

    [TestMethod]
    public void ChatPromptEditor_TransientShortcuts_DoNotInterceptAfterTextExists()
    {
        var editor = new ChatPromptEditor(
            _ => { },
            () => Assert.Fail("Help should not open."),
            () => Assert.Fail("Palette should not open."))
        {
            Text = "inspect "
        };

        Assert.IsFalse(editor.TryHandleTransientShortcutInput("?"));
        Assert.IsFalse(editor.TryHandleTransientShortcutInput("/"));
    }

    [TestMethod]
    public void ChatPromptEditor_DefaultEnterBehavior_AcceptsPrompt()
    {
        string? acceptedText = null;
        var editor = new ChatPromptEditor(text => acceptedText = text);
        var root = new VStack { editor };

        using var session = Terminal.Open(
            new InMemoryTerminalBackend(new TerminalSize(80, 20)),
            new TerminalOptions { ImplicitStartInput = true },
            force: true);
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            app.Focus(editor);
            TickTerminalApp(app);

            var backend = (InMemoryTerminalBackend)session.Instance.Backend;
            backend.PushEvent(new TerminalTextEvent { Text = "hello" });
            TickTerminalApp(app);

            backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Enter });
            TickTerminalApp(app);

            Assert.AreEqual("hello", acceptedText);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    private static AgentSystemPromptEvent CreateSystemPromptEvent(
        DateTimeOffset timestamp,
        string hash,
        string systemMessage = "system",
        string developerInstructions = "developer",
        string changeKind = "initial")
        => new(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp,
            null,
            "session_start",
            hash,
            "default",
            systemMessage,
            developerInstructions,
            new AgentSystemPromptProviderPayloadSummary("native-system-and-developer", true, false),
            null,
            new AgentSystemPromptStatistics(1, 1, 2, 6, 9),
            new AgentSystemPromptChangeSummary(changeKind, ["base/default"], [], []));

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

    private static Color GetGroupBackground(GroupStyle groupStyle)
    {
        Assert.IsNotNull(groupStyle.BackgroundStyle);
        Assert.IsTrue(groupStyle.BackgroundStyle.Value.TryGetBackground(out var background));
        return background;
    }

    private static double GetRgbDistance(Color first, Color second)
    {
        var firstRgb = first.ToRgb();
        var secondRgb = second.ToRgb();
        var redDelta = firstRgb.R - secondRgb.R;
        var greenDelta = firstRgb.G - secondRgb.G;
        var blueDelta = firstRgb.B - secondRgb.B;
        return Math.Sqrt((redDelta * redDelta) + (greenDelta * greenDelta) + (blueDelta * blueDelta)) / (255d * Math.Sqrt(3d));
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }

}

