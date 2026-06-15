using System.Collections.Immutable;
using System.Globalization;

namespace CodeAlta.Catalog;

/// <summary>
/// Lightweight string resources for i18n. English text is used as the lookup key;
/// when no translation is found, the key is returned verbatim as the English fallback.
/// </summary>
public static partial class SR
{
    /// <summary>
    /// Gets or sets the active language. Use "en" or "zh-CN".
    /// </summary>
    public static string Language
    {
        get => IsChinese(CultureInfo.CurrentUICulture.Name) ? "zh-CN" : "en";
        set => ApplyLanguage(value);
    }

    /// <summary>
    /// Looks up a translated string. Falls back to the key itself when no translation exists.
    /// </summary>
    public static string T(string key)
    {
        if (IsChinese(CultureInfo.CurrentUICulture.Name) && s_zhCn.TryGetValue(key, out var t))
        {
            return t;
        }

        return key;
    }

    /// <summary>
    /// Looks up a format string and applies arguments.
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        return args is { Length: > 0 } ? string.Format(template, args) : template;
    }

    /// <summary>
    /// Auto-detects the language from the system UI culture.
    /// </summary>
    public static void AutoDetect()
    {
        ApplyLanguage(CultureInfo.InstalledUICulture.Name);
    }

    private static void ApplyLanguage(string? languageName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(languageName))
            {
                languageName = CultureInfo.InstalledUICulture.Name;
            }

            var cultureName = IsChinese(languageName) ? "zh-CN" : "en";
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        }
    }

    private static bool IsChinese(string? languageName)
        => !string.IsNullOrWhiteSpace(languageName) && languageName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
