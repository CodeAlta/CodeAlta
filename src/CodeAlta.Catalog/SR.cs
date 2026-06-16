using System.Collections.Immutable;
using System.Globalization;
using SharpYaml.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// Lightweight string resources for i18n. English text is used as the lookup key;
/// when no translation is found, the key is returned verbatim as the English fallback.
/// </summary>
public static class SR
{
    private const string ResourceFileName = "SR.yml";

    private static readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> s_translationsByLanguage = LoadTranslations();

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
        var language = GetResourceLanguage(CultureInfo.CurrentUICulture.Name);
        return s_translationsByLanguage.TryGetValue(language, out var translations) && translations.TryGetValue(key, out var translation)
            ? translation
            : key;
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

    internal static ImmutableDictionary<string, ImmutableDictionary<string, string>> LoadTranslationsFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var reader = File.OpenText(path);
        var stream = YamlStream.Load(reader);
        if (stream.Count != 1)
        {
            throw new FormatException($"Expected exactly one YAML document in {ResourceFileName}.");
        }

        if (stream[0].Contents is not YamlMapping root)
        {
            throw new FormatException($"Expected a root mapping in {ResourceFileName}.");
        }

        var languageBuilders = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var entry in root)
        {
            var key = GetScalarValue(entry.Key, "translation key");
            if (entry.Value is not YamlMapping translationsByKey)
            {
                throw new FormatException($"Expected a translation mapping for '{key}' in {ResourceFileName}.");
            }

            foreach (var translationEntry in translationsByKey)
            {
                var language = GetScalarValue(translationEntry.Key, "language key");
                var translation = GetScalarValue(translationEntry.Value, $"'{key}' {language} translation");
                if (!languageBuilders.TryGetValue(language, out var translations))
                {
                    translations = new Dictionary<string, string>(StringComparer.Ordinal);
                    languageBuilders.Add(language, translations);
                }

                translations[key] = translation;
            }
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (language, translations) in languageBuilders)
        {
            builder.Add(language, translations.ToImmutableDictionary(StringComparer.Ordinal));
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableDictionary<string, string>> LoadTranslations()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, ResourceFileName);
        if (!File.Exists(path))
        {
            return ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
        }

        return LoadTranslationsFromFile(path);
    }

    private static string GetScalarValue(YamlElement? element, string description)
        => element is YamlValue value
            ? value.Value
            : throw new FormatException($"Expected a scalar {description} in {ResourceFileName}.");

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

    private static string GetResourceLanguage(string? languageName)
    {
        var supportedLanguage = GetSupportedLanguage(languageName);
        return string.Equals(supportedLanguage, "zh-CN", StringComparison.Ordinal) ? "zh" : supportedLanguage;
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
