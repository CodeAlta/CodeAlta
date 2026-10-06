using CodeAlta.Desktop;

namespace CodeAlta;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Started by an alta typed in a terminal, this process is the application: it leaves that terminal
        // before anything is written or started.
        var token = DesktopTerminalStart.Adopt();
        return DesktopCommandLine.Run(args, Console.Out, Console.Error,
            options => DesktopApplication.Run(token is null ? options : options with { StartToken = token }));
    }
}
