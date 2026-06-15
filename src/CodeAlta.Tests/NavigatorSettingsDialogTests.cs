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

            Assert.AreEqual(7, options.Length);
            Assert.AreEqual("Auto", options[0].ToString());
            Assert.IsNull(GetLanguageCode(options[0]));
            Assert.AreEqual("English", options[1].ToString());
            Assert.AreEqual("en", GetLanguageCode(options[1]));
            Assert.AreEqual("Español", options[2].ToString());
            Assert.AreEqual("es", GetLanguageCode(options[2]));
            Assert.AreEqual("Français", options[3].ToString());
            Assert.AreEqual("fr", GetLanguageCode(options[3]));
            Assert.AreEqual("Deutsch", options[4].ToString());
            Assert.AreEqual("de", GetLanguageCode(options[4]));
            Assert.AreEqual("日本語", options[5].ToString());
            Assert.AreEqual("ja", GetLanguageCode(options[5]));
            Assert.AreEqual("中文 (简体)", options[6].ToString());
            Assert.AreEqual("zh-CN", GetLanguageCode(options[6]));
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
        Assert.AreEqual(2, InvokeFindLanguageOptionIndex(findMethod, options, "es"));
        Assert.AreEqual(2, InvokeFindLanguageOptionIndex(findMethod, options, "es-MX"));
        Assert.AreEqual(3, InvokeFindLanguageOptionIndex(findMethod, options, "fr"));
        Assert.AreEqual(3, InvokeFindLanguageOptionIndex(findMethod, options, "fr-FR"));
        Assert.AreEqual(4, InvokeFindLanguageOptionIndex(findMethod, options, "de"));
        Assert.AreEqual(4, InvokeFindLanguageOptionIndex(findMethod, options, "de-DE"));
        Assert.AreEqual(5, InvokeFindLanguageOptionIndex(findMethod, options, "ja"));
        Assert.AreEqual(5, InvokeFindLanguageOptionIndex(findMethod, options, "ja-JP"));
        Assert.AreEqual(6, InvokeFindLanguageOptionIndex(findMethod, options, "zh-CN"));
        Assert.AreEqual(6, InvokeFindLanguageOptionIndex(findMethod, options, "zh-Hans"));
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
