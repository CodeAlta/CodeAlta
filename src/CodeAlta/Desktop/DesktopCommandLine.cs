using System.Reflection;

namespace CodeAlta.Desktop;

internal static class DesktopCommandLine
{
    internal static string Version => typeof(DesktopCommandLine).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";

    internal static int Run(string[] args, TextWriter output, TextWriter error, Func<string, int> startNative)
    {
        if (args is ["--help"] or ["-h"])
        {
            output.WriteLine("alta --data-root <new absolute directory>\nCodeAlta desktop is in development; use altatui for agent functionality.\nOnly an isolated boot surface is available; no providers, plugins or production profile are opened.\n--help / --version do not initialize native services or storage.");
            return 0;
        }

        if (args is ["--version"])
        {
            output.WriteLine($"alta {Version}");
            return 0;
        }

        try
        {
            if (args is not ["--data-root", var root] || !Path.IsPathFullyQualified(root))
            {
                error.WriteLine("An explicit new absolute --data-root is required while desktop is in development. Use --help.");
                return 2;
            }

            root = Path.GetFullPath(root);
            if (Directory.Exists(root) || File.Exists(root) || root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.TrimEnd(' ', '.').Equals(".alta", StringComparison.OrdinalIgnoreCase)))
            {
                error.WriteLine("Refusing an existing data root or a path beneath .alta. Supply a new task-owned directory.");
                return 2;
            }

            // No backend or default-profile startup until shared ownership is implemented.
            return startNative(root);
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }
}
