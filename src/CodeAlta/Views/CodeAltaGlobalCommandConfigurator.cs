using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal static class CodeAltaGlobalCommandConfigurator
{
    public static void Configure(TerminalApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.RemoveGlobalCommand("TerminalApp.Quit");
        app.AddGlobalCommand(new Command
        {
            Id = "CodeAlta.Shell.Exit",
            LabelMarkup = "Exit",
            DescriptionMarkup = "Quit CodeAlta.",
            Name = "exit",
            Presentation = CommandPresentation.CommandBar | CommandPresentation.CommandPalette,
            SearchText = "/exit quit",
            Gesture = new KeyGesture(TerminalChar.CtrlQ, TerminalModifiers.Ctrl),
            Execute = _ => app.Stop(),
        });
    }
}
