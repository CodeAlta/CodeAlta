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

    [TestMethod]
    public void Attribution_IsTheOneOfCodeAltaUntilTheUserWritesAnother_AndTheDefaultsAreNotWritten()
    {
        WithStore(store =>
        {
            Assert.AreEqual(AttributionSettings.Default, store.LoadGlobalAttributionSettings());

            const string trailer = "Assisted-by: Contoso Agent <agents@contoso.example>";
            const string instruction = "End what you post for the user with `_Posted by the Contoso agent._`.";
            store.SaveGlobalAttributionSettings(new AttributionSettings(true, "  " + trailer + " ", false, instruction));
            var expected = new AttributionSettings(true, trailer, false, instruction);
            Assert.AreEqual(expected, store.LoadGlobalAttributionSettings());
            Assert.AreEqual(expected, new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(store.ConfigPath)! }).LoadGlobalAttributionSettings());
            var text = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(text, $"co_author_trailer = \"{trailer}\"");
            StringAssert.Contains(text, "[external_writing]");
            StringAssert.Contains(text, "attribution = false");
            Assert.IsFalse(text.Contains("co_author =", StringComparison.Ordinal), text);

            // The switch of the commits keeps the trailer the user wrote.
            store.SaveGlobalCommitCoAuthor(false);
            Assert.AreEqual(expected with { CoAuthor = false }, store.LoadGlobalAttributionSettings());
            store.SaveGlobalCommitCoAuthor(true);
            Assert.AreEqual(expected, store.LoadGlobalAttributionSettings());

            // An empty text, or the one of CodeAlta, is the default: nothing is written.
            store.SaveGlobalAttributionSettings(new AttributionSettings(true, "", true, AttributionSettings.DefaultExternalWritingInstruction));
            Assert.AreEqual(AttributionSettings.Default, store.LoadGlobalAttributionSettings());
            text = File.ReadAllText(store.ConfigPath);
            Assert.IsFalse(text.Contains("[commits]", StringComparison.Ordinal) || text.Contains("[external_writing]", StringComparison.Ordinal), text);
        });
    }

    [TestMethod]
    public void Attribution_RefusesATextThatIsNotValid_AndTheConfigurationFileToo()
    {
        WithStore(store =>
        {
            foreach (var trailer in new[] { "CodeAlta helped", "Co-authored-by:", ": CodeAlta", "Co authored: CodeAlta", "-x: y", "Co-authored-by: A\nSigned-off-by: B", "Co-authored-by: " + new string('a', 200) })
            {
                Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalAttributionSettings(AttributionSettings.Default with { CoAuthorTrailer = trailer }), trailer);
            }

            foreach (var instruction in new[] { "Sign it.\nTwice.", new string('a', AttributionSettings.MaximumExternalWritingInstructionLength + 1) })
            {
                Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalAttributionSettings(AttributionSettings.Default with { ExternalWritingInstruction = instruction }));
            }

            Assert.IsFalse(File.Exists(store.ConfigPath), "Nothing is written.");
            store.SaveGlobalAttributionSettings(AttributionSettings.Default with { CoAuthorTrailer = "Assisted-by:Contoso" });
            Assert.AreEqual("Assisted-by:Contoso", store.LoadGlobalAttributionSettings().CoAuthorTrailer);

            // The editor of the file refuses what the page refuses; a file written by hand keeps its switches.
            const string invalid = "[commits]\nco_author = false\nco_author_trailer = \"CodeAlta helped\"\n";
            Assert.IsFalse(CodeAltaConfigStore.ValidateGlobalConfigContent(invalid).IsValid);
            Assert.IsTrue(CodeAltaConfigStore.ValidateGlobalConfigContent("[external_writing]\ninstruction = \"Sign it.\"\n").IsValid);
            File.WriteAllText(store.ConfigPath, invalid);
            Assert.AreEqual(AttributionSettings.Default with { CoAuthor = false }, store.LoadGlobalAttributionSettings());
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
