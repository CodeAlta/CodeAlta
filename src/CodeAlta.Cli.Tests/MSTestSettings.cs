using XenoAtom.Logging;

// Each test starts a host of its own: a few at a time keep the machine responsive.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace CodeAlta.Cli.Tests;

[TestClass]
public sealed class CliTestAssemblyHooks
{
    // The libraries log; a run in a test leaves the logging of the process to the test assembly (CliEnvironment.InitializeLogging).
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LogManager.IsInitialized) LogManager.InitializeForAsync(new LogManagerConfig());
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup() => LogManager.Shutdown();
}
