namespace CodeAlta.Catalog.Tests;

/// <summary>The setting that has the user review the changes of every session before an agent commits them.</summary>
[TestClass]
public sealed class ReviewSettingsTests
{
    [TestMethod]
    public void BeforeCommit_IsOffUntilTheFileTurnsItOn_AndOffIsNotWritten()
    {
        WithStore(store =>
        {
            Assert.IsFalse(store.LoadGlobalReviewBeforeCommit());

            store.SaveGlobalReviewBeforeCommit(true);
            Assert.IsTrue(store.LoadGlobalReviewBeforeCommit());
            Assert.IsTrue(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(store.ConfigPath)! }).LoadGlobalReviewBeforeCommit());
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "[review]");
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "before_commit = true");

            store.SaveGlobalReviewBeforeCommit(false);
            Assert.IsFalse(store.LoadGlobalReviewBeforeCommit());
            Assert.IsFalse(File.ReadAllText(store.ConfigPath).Contains("[review]", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void BeforeCommit_IsReadAsWritten_KeepsTheOtherSettings_AndABrokenFileIsOff()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.ConfigPath, "[review]\nbefore_commit = true\n");
            Assert.IsTrue(store.LoadGlobalReviewBeforeCommit());

            store.SaveGlobalSessionTitleMode(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt);
            Assert.IsTrue(store.LoadGlobalReviewBeforeCommit());
            store.SaveGlobalReviewBeforeCommit(false);
            Assert.AreEqual(CodeAlta.Agent.AgentSessionTitleMode.FirstPrompt, store.LoadGlobalSessionTitleMode());

            File.WriteAllText(store.ConfigPath, "[review\nbefore_commit = ");
            Assert.IsFalse(store.LoadGlobalReviewBeforeCommit());
        });
    }

    private static void WithStore(Action<CodeAltaConfigStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-review-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root })); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
