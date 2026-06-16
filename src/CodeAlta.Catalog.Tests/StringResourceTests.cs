using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
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
        foreach (var resource in GetTranslationResources(sourceRoot))
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

            if (Path.GetFileName(path).Equals("BuiltinShellCommands.cs", StringComparison.Ordinal))
            {
                ExtractBuiltinShellCommandStringKeys(source, builder);
            }
        }

        Assert.IsTrue(builder.Count > 0, "No SR.T string keys were found.");
        return builder.ToImmutable();
    }

    private static void ExtractBuiltinShellCommandStringKeys(string source, ISet<string> builder)
    {
        foreach (Match match in ShellCommandTextAssignmentRegex().Matches(source))
        {
            builder.Add(Regex.Unescape(match.Groups[1].Value));
        }

        foreach (Match match in ShellCommandFactoryCallRegex().Matches(source))
        {
            builder.Add(Regex.Unescape(match.Groups[2].Value));
            builder.Add(Regex.Unescape(match.Groups[3].Value));
        }
    }

    private static bool IsGeneratedOrBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}.idea{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TranslationResource> GetTranslationResources(DirectoryInfo sourceRoot)
    {
        var translationsByLanguage = SR.LoadTranslationsFromFile(Path.Combine(sourceRoot.FullName, "CodeAlta.Catalog", "SR.yml"));
        yield return new TranslationResource("de", translationsByLanguage["de"]);
        yield return new TranslationResource("es", translationsByLanguage["es"]);
        yield return new TranslationResource("fr", translationsByLanguage["fr"]);
        yield return new TranslationResource("ja", translationsByLanguage["ja"]);
        yield return new TranslationResource("zh-CN", translationsByLanguage["zh"]);
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

    [GeneratedRegex("\\b(?:Label|Description)\\s*=\\s*\\\"((?:[^\\\"\\\\]|\\\\.)*)\\\"")]
    private static partial Regex ShellCommandTextAssignmentRegex();

    [GeneratedRegex("\\b(?:Dialog|Session|ScrollMessage)\\(\\s*\\\"((?:[^\\\"\\\\]|\\\\.)*)\\\"\\s*,\\s*\\\"((?:[^\\\"\\\\]|\\\\.)*)\\\"\\s*,\\s*\\\"((?:[^\\\"\\\\]|\\\\.)*)\\\"")]
    private static partial Regex ShellCommandFactoryCallRegex();
}
