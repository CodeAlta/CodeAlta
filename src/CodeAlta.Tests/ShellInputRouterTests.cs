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
        Assert.IsInstanceOfType<OpenSessionUsageIntent>(_router.Route("/context_usage", steerRequested: false));
        Assert.IsInstanceOfType<OpenThreadInfoIntent>(_router.Route("/thread_info", steerRequested: false));
        Assert.IsInstanceOfType<OpenExpandedPromptIntent>(_router.Route("/full_prompt", steerRequested: false));
        Assert.IsInstanceOfType<SendPromptIntent>(_router.Route("/send investigate the regression", steerRequested: false));
        Assert.IsInstanceOfType<SteerPromptIntent>(_router.Route("/steer focus on tests", steerRequested: false));
        Assert.IsInstanceOfType<AbortThreadIntent>(_router.Route("/abort", steerRequested: false));
        Assert.IsInstanceOfType<ClearQueueIntent>(_router.Route("/clear_queue", steerRequested: false));
        Assert.IsInstanceOfType<CompactThreadIntent>(_router.Route("/compact", steerRequested: false));
        Assert.IsInstanceOfType<CloseTabIntent>(_router.Route("/close", steerRequested: false));
        Assert.IsInstanceOfType<QueueStatusIntent>(_router.Route("/queue", steerRequested: false));
    }

    [TestMethod]
    public void Route_CommandNames_KeepRemainingTextAsPrompt()
    {
        var sendIntent = _router.Route("/send investigate the regression", steerRequested: false);
        var steerIntent = _router.Route("/steer focus on tests", steerRequested: false);

        Assert.IsInstanceOfType<SendPromptIntent>(sendIntent);
        Assert.AreEqual("investigate the regression", ((SendPromptIntent)sendIntent).PromptText);
        Assert.IsInstanceOfType<SteerPromptIntent>(steerIntent);
        Assert.AreEqual("focus on tests", ((SteerPromptIntent)steerIntent).PromptText);
    }

    [TestMethod]
    public void Route_DelegateCommand_UsesRemainingTextAsPrompt()
    {
        var intent = _router.Route("/delegate review the test failures", steerRequested: false);

        Assert.IsInstanceOfType<DelegateThreadIntent>(intent);
        Assert.AreEqual("review the test failures", ((DelegateThreadIntent)intent).PromptText);
    }

    [TestMethod]
    public void Route_OpenCommand_PreservesOptionalInitialPath()
    {
        var intent = _router.Route(@"/open C:\code\CodeAlta", steerRequested: false);

        Assert.IsInstanceOfType<OpenFolderIntent>(intent);
        Assert.AreEqual(@"C:\code\CodeAlta", ((OpenFolderIntent)intent).InitialPath);
    }
}
