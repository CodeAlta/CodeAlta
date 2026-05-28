using CodeAlta.Frontend.Commands;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellInputRouterTests
{
    private readonly ShellInputRouter _router = new();

    [TestMethod]
    public void Route_PlainPrompt_ReturnsSendPromptIntent()
    {
        var intent = _router.Route("Investigate startup regression", steerRequested: false);

        Assert.IsInstanceOfType<SendPromptIntent>(intent);
        Assert.AreEqual("Investigate startup regression", ((SendPromptIntent)intent).PromptText);
    }

    [TestMethod]
    public void Route_BlankSteer_ReturnsSteerIntent()
    {
        var intent = _router.Route("   ", steerRequested: true);

        Assert.IsInstanceOfType<SteerPromptIntent>(intent);
        Assert.AreEqual(string.Empty, ((SteerPromptIntent)intent).PromptText);
    }

    [TestMethod]
    public void Route_QuestionMark_OpensHelp()
    {
        var intent = _router.Route("?", steerRequested: false);

        Assert.IsInstanceOfType<OpenHelpIntent>(intent);
    }

    [TestMethod]
    public void Route_TextCommands_ReturnTypedIntents()
    {
        Assert.IsInstanceOfType<OpenHelpIntent>(_router.Route("/help", steerRequested: false));
        Assert.IsInstanceOfType<OpenCommandPaletteIntent>(_router.Route("/command_palette", steerRequested: false));
        Assert.IsInstanceOfType<OpenFolderIntent>(_router.Route("/open", steerRequested: false));
        Assert.IsInstanceOfType<OpenFileEditorIntent>(_router.Route("/edit", steerRequested: false));
        Assert.IsInstanceOfType<OpenSkillsIntent>(_router.Route("/skills", steerRequested: false));
        Assert.IsInstanceOfType<OpenSkillsIntent>(_router.Route("/skill", steerRequested: false));
        Assert.IsInstanceOfType<OpenPluginsIntent>(_router.Route("/plugins", steerRequested: false));
        Assert.IsInstanceOfType<OpenPluginsIntent>(_router.Route("/plugin", steerRequested: false));
        Assert.IsInstanceOfType<OpenMcpServersIntent>(_router.Route("/mcp", steerRequested: false));
        Assert.IsInstanceOfType<OpenMcpServersIntent>(_router.Route("/mcp_servers", steerRequested: false));
        Assert.IsInstanceOfType<OpenWorkspaceSettingsIntent>(_router.Route("/settings", steerRequested: false));
        Assert.IsInstanceOfType<FocusSidebarIntent>(_router.Route("/go_to_sidebar", steerRequested: false));
        Assert.IsInstanceOfType<FocusPromptIntent>(_router.Route("/go_to_prompt", steerRequested: false));
        Assert.IsInstanceOfType<FocusModelProviderIntent>(_router.Route("/model", steerRequested: false));
        Assert.IsInstanceOfType<OpenModelsIntent>(_router.Route("/models", steerRequested: false));
        Assert.IsInstanceOfType<OpenApplicationLogsIntent>(_router.Route("/logs", steerRequested: false));
        Assert.IsInstanceOfType<OpenAboutIntent>(_router.Route("/about", steerRequested: false));
        Assert.IsInstanceOfType<OpenSessionUsageIntent>(_router.Route("/context_usage", steerRequested: false));
        Assert.IsInstanceOfType<OpenSessionInfoIntent>(_router.Route("/session_info", steerRequested: false));
        Assert.IsInstanceOfType<OpenExpandedPromptIntent>(_router.Route("/full_prompt", steerRequested: false));
        Assert.IsInstanceOfType<ExitAppIntent>(_router.Route("/exit", steerRequested: false));
        Assert.IsInstanceOfType<SendPromptIntent>(_router.Route("/send investigate the regression", steerRequested: false));
        Assert.IsInstanceOfType<AbortSessionIntent>(_router.Route("/abort", steerRequested: false));
        Assert.IsInstanceOfType<ClearQueueIntent>(_router.Route("/clear_queue", steerRequested: false));
        Assert.IsInstanceOfType<CompactSessionIntent>(_router.Route("/compact", steerRequested: false));
        Assert.IsInstanceOfType<CloseTabIntent>(_router.Route("/close", steerRequested: false));
        Assert.IsInstanceOfType<TabLeftIntent>(_router.Route("/tab_left", steerRequested: false));
        Assert.IsInstanceOfType<TabRightIntent>(_router.Route("/tab_right", steerRequested: false));
        Assert.IsInstanceOfType<MessagePreviousIntent>(_router.Route("/msg_prev", steerRequested: false));
        Assert.IsInstanceOfType<MessageNextIntent>(_router.Route("/msg_next", steerRequested: false));
        Assert.IsInstanceOfType<MessageFirstIntent>(_router.Route("/msg_first", steerRequested: false));
        Assert.IsInstanceOfType<MessageLastIntent>(_router.Route("/msg_last", steerRequested: false));
    }

    [TestMethod]
    public void Route_RemovedQueueCommand_IsUnknownTextCommand()
    {
        Assert.IsInstanceOfType<UnknownTextCommandIntent>(_router.Route("/queue", steerRequested: false));
    }

    [TestMethod]
    public void Route_AcpCommands_AreUnknownTextCommands()
    {
        Assert.IsInstanceOfType<UnknownTextCommandIntent>(_router.Route("/acp_agents", steerRequested: false));
        Assert.IsInstanceOfType<UnknownTextCommandIntent>(_router.Route("/acp", steerRequested: false));
    }

    [TestMethod]
    public void Route_SendCommand_KeepsRemainingTextAsPrompt()
    {
        var intent = _router.Route("/send investigate the regression", steerRequested: false);

        Assert.IsInstanceOfType<SendPromptIntent>(intent);
        Assert.AreEqual("investigate the regression", ((SendPromptIntent)intent).PromptText);
    }

    [TestMethod]
    public void Route_AllAdvertisedSlashCommands_AreRoutable()
    {
        foreach (var binding in ShellCommandCatalog.Commands
                     .Where(static command => command.ShowInHelp)
                     .SelectMany(static command => command.HelpBindings)
                     .Where(static binding => binding.Length > 1 && binding[0] == '/'))
        {
            var intent = _router.Route($"{binding} sample", steerRequested: false);
            Assert.IsFalse(intent is UnknownTextCommandIntent, $"Help advertises an unroutable slash command: {binding}");
        }
    }

    [TestMethod]
    public void Route_KeyboardOnlyCommands_AreTreatedAsPlainPromptText()
    {
        var steerIntent = _router.Route("/steer focus on tests", steerRequested: false);

        Assert.IsInstanceOfType<SendPromptIntent>(steerIntent);
        Assert.AreEqual("/steer focus on tests", ((SendPromptIntent)steerIntent).PromptText);
    }

    [TestMethod]
    public void Route_KeyboardOnlyCommands_StayPlainTextDuringSteerSubmission()
    {
        var intent = _router.Route("/steer focus on tests", steerRequested: true);

        Assert.IsInstanceOfType<SteerPromptIntent>(intent);
        Assert.AreEqual("/steer focus on tests", ((SteerPromptIntent)intent).PromptText);
    }

    [TestMethod]
    public void Route_OpenCommand_PreservesOptionalInitialPath()
    {
        var intent = _router.Route(@"/open C:\code\CodeAlta", steerRequested: false);

        Assert.IsInstanceOfType<OpenFolderIntent>(intent);
        Assert.AreEqual(@"C:\code\CodeAlta", ((OpenFolderIntent)intent).InitialPath);
    }
}
