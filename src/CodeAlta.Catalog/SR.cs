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
    /// Gets or sets the active language. Use "auto", "en", "es", "fr", "de", "ja", or "zh-CN".
    /// </summary>
    public static string Language
    {
        get => GetSupportedLanguage(CultureInfo.CurrentUICulture.Name);
        set => ApplyLanguage(value);
    }

    /// <summary>
    /// Looks up a translated string. Falls back to the key itself when no translation exists.
    /// </summary>
    public static string T(string key)
    {
        return GetSupportedLanguage(CultureInfo.CurrentUICulture.Name) switch
        {
            "de" when s_de.TryGetValue(key, out var t) => t,
            "es" when s_es.TryGetValue(key, out var t) => t,
            "fr" when s_fr.TryGetValue(key, out var t) => t,
            "ja" when s_ja.TryGetValue(key, out var t) => t,
            "zh-CN" when s_zhCn.TryGetValue(key, out var t) => t,
            _ => key,
        };
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
            languageName = languageName?.Trim();
            if (string.IsNullOrWhiteSpace(languageName) || string.Equals(languageName, "auto", StringComparison.OrdinalIgnoreCase))
            {
                languageName = CultureInfo.InstalledUICulture.Name;
            }

            var cultureName = GetSupportedLanguage(languageName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        }
    }

    private static string GetSupportedLanguage(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return "en";
        }

        var trimmed = languageName.Trim();
        if (trimmed.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            return "de";
        }

        if (trimmed.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            return "es";
        }

        if (trimmed.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return "fr";
        }

        if (trimmed.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }

        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        return "en";
    }
}
