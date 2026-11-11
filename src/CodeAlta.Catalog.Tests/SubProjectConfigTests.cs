namespace CodeAlta.Catalog.Tests;

/// <summary>The configuration of a sub-project: the one of its repository, then its own on top.</summary>
[TestClass]
public sealed class SubProjectConfigTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-subproject-config-" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
    }

    private static void Config(string folder, string toml)
    {
        Directory.CreateDirectory(Path.Combine(folder, ".alta"));
        File.WriteAllText(Path.Combine(folder, ".alta", "config.toml"), toml);
    }

    [TestMethod]
    public void ASubProjectReadsTheConfigurationOfItsRepository_ItsOwnOnTop_AndWhatTheRepositoryTurnsOffStaysOff()
    {
        var repository = Path.Combine(_root, "contoso");
        var billing = Directory.CreateDirectory(Path.Combine(repository, "services", "billing")).FullName;
        var core = Directory.CreateDirectory(Path.Combine(billing, "core")).FullName;
        Config(repository, "[skills]\ndisabled = [\"legacy\"]\n\n[plugins.jira]\nenabled = false\n\n[plugins.git]\nenabled = true\n\n[review]\nbefore_commit = true\n");
        Config(billing, "[skills]\ndisabled = [\"draft\"]\n");
        Config(core, "[plugins.jira]\nenabled = true\n\n[plugins.git]\nenabled = false\n\n[review]\nbefore_commit = false\n");
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(_root, "alta") });

        var config = store.LoadProject(core, [billing, repository]);
        CollectionAssert.AreEquivalent(new[] { "legacy", "draft" }, config.Skills!.Disabled!.ToArray(), "the skills each folder disables");
        Assert.IsFalse(config.Plugins!["jira"].Enabled, "what the repository turns off stays off");
        Assert.IsFalse(config.Plugins["git"].Enabled, "what the sub-project turns off");
        Assert.IsNull(config.Review, "its own value of a setting wins: `before_commit = false`, the default, which the document leaves out");

        Assert.IsTrue(store.LoadProject(core).Plugins!["jira"].Enabled, "its own file alone, as before");
        Assert.IsTrue(store.LoadProject(core, []).Plugins!["jira"].Enabled, "a project that is no sub-project reads its own file");
        Assert.IsTrue(store.LoadProject(Directory.CreateDirectory(Path.Combine(repository, "apps", "web")).FullName, [repository]).Review!.BeforeCommit,
            "a sub-project without a file of its own has the configuration of its repository");
    }
}
