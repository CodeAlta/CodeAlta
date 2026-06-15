using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed partial class StringResourceTests
{
    [TestMethod]
    public void ResourcesCoverAllTranslatedStringKeys()
    {
        var sourceRoot = FindSourceRoot();
        var resourceKeys = ExtractTranslatedStringKeys(sourceRoot);
        foreach (var resource in GetTranslationResources())
        {
            var missing = resourceKeys
                .Where(key => !resource.Translations.ContainsKey(key))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var empty = resourceKeys
                .Where(key => resource.Translations.TryGetValue(key, out var translation) && string.IsNullOrWhiteSpace(translation))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.AreEqual(0, missing.Length, $"Missing {resource.LanguageName} translations for: " + string.Join(", ", missing));
            Assert.AreEqual(0, empty.Length, $"Empty {resource.LanguageName} translations for: " + string.Join(", ", empty));
        }
    }

    [TestMethod]
    public void ChineseLanguageUsesChineseTranslations()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            SR.Language = "zh-CN";

            Assert.AreEqual("默认", SR.T("Default"));
            Assert.AreEqual("思考中…", SR.T("Thinking..."));
            Assert.AreEqual("下一个会话将在 CodeAlta 中启动。", SR.T("Next session will start in {0}.", "CodeAlta"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [TestMethod]
    [DataRow("de-DE", "de")]
    [DataRow("es-MX", "es")]
    [DataRow("fr-CA", "fr")]
    [DataRow("ja-JP", "ja")]
    [DataRow("zh-Hans", "zh-CN")]
    [DataRow("en-US", "en")]
    [DataRow("it-IT", "en")]
    public void LanguageSelectsSupportedCulture(string requestedLanguage, string expectedLanguage)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            SR.Language = requestedLanguage;

            Assert.AreEqual(expectedLanguage, SR.Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [TestMethod]
    public void AutoLanguageUsesInstalledUiCulture()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            SR.Language = "auto";

            var expectedLanguage = GetExpectedLanguage(CultureInfo.InstalledUICulture.Name);
            Assert.AreEqual(expectedLanguage, SR.Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    private static DirectoryInfo FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CodeAlta.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        Assert.Fail("Could not locate CodeAlta.slnx from the test output directory.");
        throw new UnreachableException();
    }

    private static ImmutableSortedSet<string> ExtractTranslatedStringKeys(DirectoryInfo sourceRoot)
    {
        var builder = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(sourceRoot.FullName, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrBuildOutput(path))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match match in SrTCallRegex().Matches(source))
            {
                builder.Add(Regex.Unescape(match.Groups[1].Value));
            }
        }

        Assert.IsTrue(builder.Count > 0, "No SR.T string keys were found.");
        return builder.ToImmutable();
    }

    private static bool IsGeneratedOrBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}.idea{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TranslationResource> GetTranslationResources()
    {
        yield return new TranslationResource("de", GetTranslations("s_de"));
        yield return new TranslationResource("es", GetTranslations("s_es"));
        yield return new TranslationResource("fr", GetTranslations("s_fr"));
        yield return new TranslationResource("ja", GetTranslations("s_ja"));
        yield return new TranslationResource("zh-CN", GetTranslations("s_zhCn"));
    }

    private static IReadOnlyDictionary<string, string> GetTranslations(string fieldName)
    {
        var field = typeof(SR).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field, $"Could not find the {fieldName} translation table.");
        var translations = field.GetValue(null) as IReadOnlyDictionary<string, string>;
        Assert.IsNotNull(translations, $"The {fieldName} translation table has an unexpected type.");
        return translations;
    }

    private static string GetExpectedLanguage(string languageName)
    {
        if (languageName.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            return "de";
        }

        if (languageName.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            return "es";
        }

        if (languageName.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return "fr";
        }

        if (languageName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }

        if (languageName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        return "en";
    }

    private sealed record TranslationResource(string LanguageName, IReadOnlyDictionary<string, string> Translations);

    [GeneratedRegex("SR\\.T\\(\\\"((?:[^\\\"\\\\]|\\\\.)*)\\\"")]
    private static partial Regex SrTCallRegex();
}
