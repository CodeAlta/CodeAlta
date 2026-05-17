using CodeAlta.Frontend.Commands;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellInputCoordinatorTests
{
    [TestMethod]
    [DataRow("/help providers", typeof(OpenHelpCommand))]
    [DataRow("/providers", typeof(OpenModelProvidersCommand))]
    [DataRow("/settings", typeof(OpenWorkspaceSettingsCommand))]
    [DataRow("/model", typeof(FocusModelProviderCommand))]
    [DataRow("/sidebar", typeof(FocusSidebarCommand))]
    [DataRow("/prompt", typeof(FocusPromptCommand))]
    [DataRow("/close", typeof(CloseCurrentTabCommand))]
    [DataRow("/tab_left", typeof(SelectRelativeTabCommand))]
    [DataRow("/msg_next", typeof(ScrollSelectedThreadMessageCommand))]
    [DataRow("/queue", typeof(ShowQueueStatusCommand))]
    [DataRow("/does_not_exist arg", typeof(ExecutePluginTextCommand))]
    public async Task HandleInputAsync_DispatchesRoutedCommand(string input, Type expectedCommandType)
    {
        var dispatcher = new CapturingCommandDispatcher();
        var coordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            () => input,
            () => true,
            dispatcher);

        await coordinator.HandleInputAsync(input, steer: false);

        Assert.AreEqual(expectedCommandType, dispatcher.Commands.Single().GetType());
    }

    [TestMethod]
    public async Task HandleInputAsync_DispatchesPromptWhenTextIsNotSlashCommand()
    {
        var dispatcher = new CapturingCommandDispatcher();
        var coordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            () => "hello",
            () => true,
            dispatcher);

        await coordinator.HandleInputAsync("hello", steer: false);

        var command = (SubmitPromptCommand)dispatcher.Commands.Single();
        Assert.AreEqual("hello", command.Text);
        Assert.IsFalse(command.Steer);
    }

    [TestMethod]
    public async Task HandleInputAsync_DispatchesCurrentPromptWhenAcceptedInputIsEmptyButPromptHasDraft()
    {
        var dispatcher = new CapturingCommandDispatcher();
        var coordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            () => "draft text",
            () => false,
            dispatcher);

        await coordinator.HandleInputAsync(null, steer: false);

        var command = (SubmitPromptCommand)dispatcher.Commands.Single();
        Assert.IsNull(command.Text);
        Assert.IsFalse(command.Steer);
    }

    [TestMethod]
    public async Task HandleInputAsync_DoesNotDispatchWhenAcceptedInputIsEmptyAndPromptIsEmpty()
    {
        var dispatcher = new CapturingCommandDispatcher();
        var coordinator = new ShellInputCoordinator(
            new ShellInputRouter(),
            () => string.Empty,
            () => true,
            dispatcher);

        await coordinator.HandleInputAsync(null, steer: false);

        Assert.AreEqual(0, dispatcher.Commands.Count);
    }

    [TestMethod]
    public void BuildUnknownCommandStatus_PreservesLegacyMessage()
    {
        var message = ShellCommandSurfaceCoordinator.BuildUnknownCommandStatus("missing");

        Assert.AreEqual("Unknown command '/missing'. Press F1 or type /help.", message);
    }

    private sealed class CapturingCommandDispatcher : IShellCommandDispatcher
    {
        public List<ShellCommand> Commands { get; } = [];

        public ValueTask DispatchAsync(ShellCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.CompletedTask;
        }
    }
}
