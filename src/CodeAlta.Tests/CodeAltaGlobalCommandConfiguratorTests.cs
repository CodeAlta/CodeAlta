using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaGlobalCommandConfiguratorTests
{
    [TestMethod]
    public void Configure_ReplacesFrameworkQuitCommandWithCodeAltaExitCommand()
    {
        var app = new TerminalApp(new TextBlock("CodeAlta"));

        CodeAltaGlobalCommandConfigurator.Configure(app);

        Assert.IsFalse(app.GlobalCommands.Any(command => string.Equals(command.Id, "TerminalApp.Quit", StringComparison.Ordinal)));
        var exitCommand = app.GlobalCommands.Single(command => string.Equals(command.Id, "CodeAlta.Shell.Exit", StringComparison.Ordinal));
        Assert.AreEqual("/exit", exitCommand.LabelMarkup);
        Assert.AreEqual("exit", exitCommand.Name);
        Assert.AreEqual(CommandPresentation.CommandBar | CommandPresentation.CommandPalette, exitCommand.Presentation);
        Assert.AreEqual(new KeyGesture(TerminalChar.CtrlQ, TerminalModifiers.Ctrl), exitCommand.Gesture);
        StringAssert.Contains(exitCommand.SearchText, "/exit");
    }
}
