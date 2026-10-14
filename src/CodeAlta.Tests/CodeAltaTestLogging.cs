using System.Globalization;
using XenoAtom.Logging;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaTestLoggingAssemblyHooks
{
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Tests assert English text and invariant-looking numbers: run them in en-US whatever the display language of the machine.
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        CodeAltaTestLogging.InitializeFallback();
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        LogManager.Shutdown();
    }
}

internal static class CodeAltaTestLogging
{
    public static void InitializeFallback()
    {
        if (LogManager.IsInitialized)
        {
            return;
        }

        try
        {
            LogManager.InitializeForAsync(new LogManagerConfig());
        }
        catch (InvalidOperationException) when (LogManager.IsInitialized)
        {
        }
    }
}
