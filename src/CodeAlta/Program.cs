using CodeAlta.Desktop;

namespace CodeAlta;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => DesktopCommandLine.Run(args, Console.Out, Console.Error, DesktopApplication.Run);
}
