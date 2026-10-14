using System.Globalization;

namespace CodeAlta.Plugin.Statistics.Tests;

// Tests assert English text and invariant-looking numbers: run them in en-US whatever the display language of the machine.
[TestClass]
public sealed class TestCulture
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
