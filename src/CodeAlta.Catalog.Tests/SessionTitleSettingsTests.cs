using CodeAlta.Agent;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionTitleSettingsTests
{
    [TestMethod]
    public void TitleMode_IsTheDefaultUntilTheFileNamesOne_AndTheDefaultIsNotWritten()
    {
        WithStore(store =>
        {
            Assert.AreEqual(AgentSessionTitles.DefaultMode, store.LoadGlobalSessionTitleMode());

            foreach (var mode in Enum.GetValues<AgentSessionTitleMode>())
            {
                store.SaveGlobalSessionTitleMode(mode);
                Assert.AreEqual(mode, store.LoadGlobalSessionTitleMode());
                Assert.AreEqual(mode, new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(store.ConfigPath)! }).LoadGlobalSessionTitleMode());
                var text = File.ReadAllText(store.ConfigPath);
                if (mode == AgentSessionTitles.DefaultMode)
                {
                    Assert.IsFalse(text.Contains("[sessions]", StringComparison.Ordinal), text);
                }
                else
                {
                    StringAssert.Contains(text, "[sessions]");
                    StringAssert.Contains(text, $"title = \"{AgentSessionTitles.NameOf(mode)}\"");
                }
            }
        });
    }

    [TestMethod]
    public void TitleMode_IsReadAsWritten_AndWhatIsNotAModeIsTheDefault()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.ConfigPath, "[sessions]\ntitle = \" Last_Message \"\n");
            Assert.AreEqual(AgentSessionTitleMode.LastMessage, store.LoadGlobalSessionTitleMode());

            File.WriteAllText(store.ConfigPath, "[sessions]\ntitle = \"something else\"\n");
            Assert.AreEqual(AgentSessionTitles.DefaultMode, store.LoadGlobalSessionTitleMode());

            File.WriteAllText(store.ConfigPath, "[sessions\ntitle = ");
            Assert.AreEqual(AgentSessionTitles.DefaultMode, store.LoadGlobalSessionTitleMode());
        });
    }

    [TestMethod]
    public void SavingTheTitleMode_KeepsTheOtherSettings()
    {
        WithStore(store =>
        {
            store.SaveGlobalWorkItemSettings(WorkItems.WorkItemSettings.Default with { Propose = false });
            store.SaveGlobalSessionTitleMode(AgentSessionTitleMode.FirstPrompt);
            Assert.IsFalse(WorkItems.WorkItemSettings.Read(store.LoadGlobal().WorkItems).Propose);

            store.SaveGlobalWorkItemSettings(WorkItems.WorkItemSettings.Default);
            Assert.AreEqual(AgentSessionTitleMode.FirstPrompt, store.LoadGlobalSessionTitleMode());
        });
    }

    private static void WithStore(Action<CodeAltaConfigStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-titles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root })); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
