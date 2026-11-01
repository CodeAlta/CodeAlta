namespace CodeAlta.Catalog.Tests;

/// <summary>Tests of the comments of <c>config.toml</c> through the typed saves of the settings.</summary>
[TestClass]
public sealed class ConfigCommentsTests
{
    private const string Commented = """
        # My CodeAlta configuration
        # kept by hand

        # How sessions behave
        [sessions]
        # The title of a new session
        title = "model"
        prompt_suggestions = true # suggest

        # Fallback when a provider is out of quota
        [models.fallback]
        # resume once the quota resets
        resume = true

        # Things I keep
        [custom]
        keep = 1
        # the end
        """;

    private static readonly string[] Comments =
    [
        "# My CodeAlta configuration", "# kept by hand", "# How sessions behave", "# The title of a new session", "# suggest",
        "# Fallback when a provider is out of quota", "# resume once the quota resets", "# Things I keep", "# the end",
    ];

    [TestMethod]
    public void ATypedSave_KeepsTheCommentsOnLinesOfTheirOwn()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-config-comments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(new CatalogOptions { GlobalRoot = root });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(CatalogOptions options)
    {
        File.WriteAllText(options.ConfigPath, Commented);
        var store = new CodeAltaConfigStore(options);

        store.SaveGlobalPromptSuggestions(false);
        var models = store.LoadGlobalModelSettings();
        store.SaveGlobalModelSettings(models with { Fallback = models.Fallback with { OnQuota = false } });

        var text = File.ReadAllText(options.ConfigPath);
        foreach (var comment in Comments) StringAssert.Contains(text, comment, $"the comment is kept:\n{text}");
        Assert.AreEqual(1, Count(text, "# How sessions behave"), text);
        // Each comment stays where it was: before its table, before its setting; the comment of the title, which went back
        // to its default and left the file, before the setting that followed it; the last one at the end.
        AssertOrder(text, "# My CodeAlta configuration", "# kept by hand", "# How sessions behave", "[sessions]", "# The title of a new session", "prompt_suggestions = false # suggest",
            "# Fallback when a provider is out of quota", "[models.fallback]", "# resume once the quota resets", "resume = true", "# Things I keep", "[custom]", "keep = 1", "# the end");
        Assert.IsFalse(store.LoadGlobalPromptSuggestions());
        Assert.IsFalse(store.LoadGlobalModelSettings().Fallback.OnQuota);
        Assert.IsTrue(store.LoadGlobalModelSettings().Fallback.Resume);

        // A second save keeps them, once.
        var saved = store.LoadGlobalModelSettings();
        store.SaveGlobalModelSettings(saved with { Fallback = saved.Fallback with { Enabled = true } });
        var again = File.ReadAllText(options.ConfigPath);
        foreach (var comment in Comments) Assert.AreEqual(1, Count(again, comment), $"{comment}:\n{again}");
    }

    [TestMethod]
    public void AFileWithoutComments_IsSavedAsBefore()
    {
        const string plain = "[sessions]\nprompt_suggestions = true\n";
        var saved = "[sessions]\nprompt_suggestions = false\n";
        Assert.AreSame(saved, ConfigComments.Restore(plain, saved));
        Assert.AreEqual("not toml [", ConfigComments.Restore("# a comment\n[sessions]\n", "not toml ["), "a text that cannot be read is left as it is");
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void AssertOrder(string text, params string[] parts)
    {
        var position = -1;
        foreach (var part in parts)
        {
            var next = text.IndexOf(part, position + 1, StringComparison.Ordinal);
            Assert.IsGreaterThan(position, next, $"'{part}' comes next:\n{text}");
            position = next;
        }
    }
}
