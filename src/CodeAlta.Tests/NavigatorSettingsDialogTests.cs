using System.Globalization;
using System.Reflection;
using CodeAlta.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class NavigatorSettingsDialogTests
{
    [TestMethod]
    public void LanguageOptions_DefaultToAutoAndExposeSpecificOverrides()
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

            var options = GetLanguageOptions();

            Assert.AreEqual(3, options.Length);
            Assert.AreEqual("Auto", options[0].ToString());
            Assert.IsNull(GetLanguageCode(options[0]));
            Assert.AreEqual("English", options[1].ToString());
            Assert.AreEqual("en", GetLanguageCode(options[1]));
            Assert.AreEqual("中文 (简体)", options[2].ToString());
            Assert.AreEqual("zh-CN", GetLanguageCode(options[2]));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [TestMethod]
    public void FindLanguageOptionIndex_MapsAutoAndCultureSpecificOverrides()
    {
        var options = GetLanguageOptionsObject();
        var findMethod = typeof(NavigatorSettingsDialog).GetMethod("FindLanguageOptionIndex", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(findMethod);

        Assert.AreEqual(0, InvokeFindLanguageOptionIndex(findMethod, options, null));
        Assert.AreEqual(0, InvokeFindLanguageOptionIndex(findMethod, options, "auto"));
        Assert.AreEqual(1, InvokeFindLanguageOptionIndex(findMethod, options, "en"));
        Assert.AreEqual(1, InvokeFindLanguageOptionIndex(findMethod, options, "en-US"));
        Assert.AreEqual(2, InvokeFindLanguageOptionIndex(findMethod, options, "zh-CN"));
        Assert.AreEqual(2, InvokeFindLanguageOptionIndex(findMethod, options, "zh-Hans"));
    }

    private static object GetLanguageOptionsObject()
    {
        var createMethod = typeof(NavigatorSettingsDialog).GetMethod("CreateLanguageOptions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(createMethod);
        return createMethod.Invoke(null, null)!;
    }

    private static object[] GetLanguageOptions()
        => ((IEnumerable<object>)GetLanguageOptionsObject()).ToArray();

    private static int InvokeFindLanguageOptionIndex(MethodInfo findMethod, object options, string? languageName)
        => (int)findMethod.Invoke(null, [options, languageName])!;

    private static string? GetLanguageCode(object option)
        => (string?)option.GetType().GetProperty("LanguageCode")?.GetValue(option);
}
