using System.Globalization;
using XenoAtom.Logging;

namespace CodeAlta.Hosting.Tests;

// Writerless, test-process-only logging: no production logging/profile initialization.
// TestContext retains ownership so cleanup never shuts down a borrowed logger configuration.
[TestClass]
public sealed class HostingTestLogging
{
    private const string OwnershipKey = "CodeAlta.Hosting.Tests.OwnsLogging";

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        // Tests assert English text and invariant-looking numbers: run them in en-US whatever the display language of the machine.
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

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
        if (context.Properties[OwnershipKey] is true)
        {
            LogManager.Shutdown();
        }
    }
}
