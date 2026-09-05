using NeoAstra;

namespace CodeAlta.Desktop.Probe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("alta-desktop-probe [--smoke] --data-root <new absolute directory>\nFake-data M0 probe only. --smoke has a 60-second deadline; no production CodeAlta services.\n--help / --version do not initialize native services or storage.");
            return 0;
        }

        if (args is ["--version"])
        {
            Console.WriteLine("alta-desktop-probe 0.1.0 (NeoAstra 0.1.0)");
            return 0;
        }

        var smoke = args.Length == 3 && args[0] == "--smoke";
        var options = smoke ? args[1..] : args;
        if (options is not ["--data-root", var root] || !Path.IsPathFullyQualified(root))
        {
            Console.Error.WriteLine("An explicit new absolute --data-root is required. Use --help.");
            return 2;
        }

        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || File.Exists(root) || root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals(".alta", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Refusing an existing data root or a path beneath .alta. Supply a new probe-only directory.");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(root);
            using var evidence = new StreamWriter(new FileStream(Path.Combine(root, "smoke.log"), FileMode.CreateNew)) { AutoFlush = true };
            void Log(string message)
            {
                Console.WriteLine(message);
                evidence.WriteLine(message);
            }

            var probe = new ProbeApplication(root, smoke, Log);
            try
            {
                var result = NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "CodeAlta Desktop Probe",
                    // Retain the dispatcher until async view/channel cleanup after native close completes.
                    ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, probe.RunAsync);
                Log($"EXIT native={result} probe={probe.ExitCode}");
                return result == 0 ? probe.ExitCode : result;
            }
            catch (Exception exception)
            {
                Log($"FAIL {exception}");
                return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
