namespace CodeAlta.Catalog.Tests;

/// <summary>The setting that has agents name CodeAlta as a co-author of their commits.</summary>
[TestClass]
public sealed class CommitSettingsTests
{
    [TestMethod]
    public void CoAuthor_IsOnUntilTheFileTurnsItOff_AndOnIsNotWritten()
    {
        WithStore(store =>
        {
            Assert.IsTrue(store.LoadGlobalCommitCoAuthor());

            store.SaveGlobalCommitCoAuthor(false);
            Assert.IsFalse(store.LoadGlobalCommitCoAuthor());
            Assert.IsFalse(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(store.ConfigPath)! }).LoadGlobalCommitCoAuthor());
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "[commits]");
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "co_author = false");

            store.SaveGlobalCommitCoAuthor(true);
            Assert.IsTrue(store.LoadGlobalCommitCoAuthor());
            Assert.IsFalse(File.ReadAllText(store.ConfigPath).Contains("[commits]", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void CoAuthor_IsReadAsWritten_KeepsTheOtherSettings_AndABrokenFileIsOn()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.ConfigPath, "[commits]\nco_author = false\n");
            Assert.IsFalse(store.LoadGlobalCommitCoAuthor());

            store.SaveGlobalReviewBeforeCommit(true);
            Assert.IsFalse(store.LoadGlobalCommitCoAuthor());
            store.SaveGlobalCommitCoAuthor(true);
            Assert.IsTrue(store.LoadGlobalReviewBeforeCommit());

            File.WriteAllText(store.ConfigPath, "[commits\nco_author = ");
            Assert.IsTrue(store.LoadGlobalCommitCoAuthor());
        });
    }

    private static void WithStore(Action<CodeAltaConfigStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-commit-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root })); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
