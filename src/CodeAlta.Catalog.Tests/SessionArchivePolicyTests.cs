using CodeAlta.Agent;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionArchivePolicyTests
{
    [TestMethod]
    public void Policies_AreReadFromTheUserFileAndFromTheFileOfAProject()
    {
        WithStore((store, root) =>
        {
            Assert.AreEqual(0, store.LoadGlobalSessionArchivePolicies().Count);
            File.WriteAllText(store.ConfigPath, """
                [[sessions.archive_policies]]
                labels = ["Audit", "pr-review", "Not a label!"]
                older_than_days = 7

                [[sessions.archive_policies]]
                automations = ["Nightly review", " nightly review "]
                keep_last = 10
                """);
            var global = store.LoadGlobalSessionArchivePolicies();
            Assert.HasCount(2, global);
            CollectionAssert.AreEqual(new[] { "audit", "pr-review" }, global[0].Labels.ToArray(), "Ids are read in lower case, each once; what is no id is left out.");
            Assert.AreEqual(TimeSpan.FromDays(7), global[0].OlderThan);
            Assert.IsNull(global[0].KeepLast);
            CollectionAssert.AreEqual(new[] { "Nightly review" }, global[1].Automations.ToArray());
            Assert.AreEqual(10, global[1].KeepLast);

            var project = Directory.CreateDirectory(Path.Combine(root, "project", ".alta")).Parent!.FullName;
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), """
                [sessions]
                archive_policies = [{ labels = ["sre"], older_than_days = 3, keep_last = 5 }]
                """);
            var own = store.LoadProjectSessionArchivePolicies(project);
            Assert.HasCount(1, own);
            Assert.AreEqual((TimeSpan.FromDays(3), 5), (own[0].OlderThan!.Value, own[0].KeepLast!.Value));
            Assert.AreEqual(0, store.LoadProjectSessionArchivePolicies(Path.Combine(root, "missing")).Count);
        });
    }

    [TestMethod]
    public void APolicy_ThatWouldArchiveWhatNobodyChose_IsNotRead()
    {
        Assert.IsFalse(SessionArchivePolicy.TryRead(new() { OlderThanDays = 7 }, out _, out var noSessions));
        StringAssert.Contains(noSessions, "labels");
        Assert.IsFalse(SessionArchivePolicy.TryRead(new() { Labels = ["audit"] }, out _, out var noWhen));
        StringAssert.Contains(noWhen, "older_than_days");
        Assert.IsFalse(SessionArchivePolicy.TryRead(new() { Labels = ["audit"], OlderThanDays = 0 }, out _, out _));
        Assert.IsFalse(SessionArchivePolicy.TryRead(new() { Labels = ["audit"], KeepLast = 0 }, out _, out _));
        Assert.IsTrue(SessionArchivePolicy.TryRead(new() { Labels = ["audit"], KeepLast = 1 }, out var policy, out _));
        Assert.AreEqual(1, policy!.KeepLast);
        Assert.HasCount(1, SessionArchivePolicy.ReadAll([new() { Labels = ["audit"] }, null, new() { Labels = ["audit"], OlderThanDays = 1 }]));
    }

    [TestMethod]
    public void ASettingSavedFromTheWindow_KeepsThePolicies()
    {
        WithStore((store, _) =>
        {
            File.WriteAllText(store.ConfigPath, "[[sessions.archive_policies]]\nlabels = [\"audit\"]\nolder_than_days = 7\n");
            store.SaveGlobalSessionTitleMode(AgentSessionTitleMode.FirstPrompt);
            store.SaveGlobalSessionTitleMode(AgentSessionTitles.DefaultMode);
            var policies = store.LoadGlobalSessionArchivePolicies();
            Assert.HasCount(1, policies, File.ReadAllText(store.ConfigPath));
            CollectionAssert.AreEqual(new[] { "audit" }, policies[0].Labels.ToArray());
        });
    }

    private static void WithStore(Action<CodeAltaConfigStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-archive-policies-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = global }), root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
