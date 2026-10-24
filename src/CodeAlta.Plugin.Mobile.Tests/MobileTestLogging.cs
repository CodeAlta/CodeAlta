using XenoAtom.Logging;

namespace CodeAlta.Plugin.Mobile.Tests;

// Writerless, test-process-only logging: the plugin logs what it does with its server and Tailscale.
[TestClass]
public sealed class MobileTestLogging
{
    private const string OwnershipKey = "CodeAlta.Plugin.Mobile.Tests.OwnsLogging";

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Properties[OwnershipKey] = false;
        if (!LogManager.IsInitialized)
        {
            LogManager.InitializeForAsync(new LogManagerConfig());
            context.Properties[OwnershipKey] = true;
        }
    }

    [AssemblyCleanup]
    public static void Cleanup(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Properties[OwnershipKey] is true)
        {
            LogManager.Shutdown();
        }
    }
}
