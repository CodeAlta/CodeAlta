using XenoAtom.Logging;

// Each test starts a host of its own: a few at a time keep the machine responsive.
[assembly: Parallelize(Workers = 2, Scope = ExecutionScope.MethodLevel)]

namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class NodeTestAssemblyHooks
{
    // The libraries log; the tests leave the logging of the process to the assembly (NodeApplication.InitializeLogging).
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LogManager.IsInitialized) LogManager.InitializeForAsync(new LogManagerConfig());
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup() => LogManager.Shutdown();
}
