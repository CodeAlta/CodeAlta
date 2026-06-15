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
    public void ChineseResourcesCoverAllTranslatedStringKeys()
    {
        var sourceRoot = FindSourceRoot();
        var resourceKeys = ExtractTranslatedStringKeys(sourceRoot);
        var translations = GetChineseTranslations();
        var missing = resourceKeys
            .Where(key => !translations.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var empty = resourceKeys
            .Where(key => translations.TryGetValue(key, out var translation) && string.IsNullOrWhiteSpace(translation))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(0, missing.Length, "Missing zh-CN translations for: " + string.Join(", ", missing));
        Assert.AreEqual(0, empty.Length, "Empty zh-CN translations for: " + string.Join(", ", empty));
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

    private static IReadOnlyDictionary<string, string> GetChineseTranslations()
    {
        var field = typeof(SR).GetField("s_zhCn", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field, "Could not find the zh-CN translation table.");
        var translations = field.GetValue(null) as IReadOnlyDictionary<string, string>;
        Assert.IsNotNull(translations, "The zh-CN translation table has an unexpected type.");
        return translations;
    }

    [GeneratedRegex("SR\\.T\\(\\\"((?:[^\\\"\\\\]|\\\\.)*)\\\"")]
    private static partial Regex SrTCallRegex();
}
