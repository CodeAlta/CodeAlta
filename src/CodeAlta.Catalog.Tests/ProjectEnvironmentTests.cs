namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectEnvironmentTests
{
    private string _root = null!;
    private CodeAltaConfigStore _config = null!;
    private CatalogOptions _options = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Directory.CreateTempSubdirectory("codealta-project-env-").FullName;
        _options = new CatalogOptions { GlobalRoot = Path.Combine(_root, "alta") };
        _config = new CodeAltaConfigStore(_options);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void ReadsValidValuesAndTheEnvironmentInheritedFromItsRepository()
    {
        var repository = Path.Combine(_root, "repo");
        var project = Path.Combine(repository, "app");
        _options.InheritedProjectRoots = path => path == project ? [repository] : [];
        WriteConfig(repository, """
            [env]
            NODE_ENV = "production"
            GH_TOKEN = "${secret:ACME_GH_TOKEN}"
            GRAFANA_URL = "${var:GRAFANA_URL}"
            """);
        WriteConfig(project, """
            [env]
            NODE_ENV = "development"
            APP_SETTINGS = "~/.app/${env:USER}"
            APP_API_KEY = "not-a-reference"
            APP_TOKEN = "${var:PLAIN_VALUE}"
            GIT_CONFIG_VALUE_0 = "reserved"
            BAD = "${unknown:VALUE}"
            NOT_TEXT = 42
            """);

        var variables = _config.LoadProjectEnvironment(project).ToDictionary();
        CollectionAssert.AreEquivalent(new[] { "NODE_ENV", "GH_TOKEN", "GRAFANA_URL", "APP_SETTINGS" }, variables.Keys.ToArray());
        Assert.AreEqual("development", variables["NODE_ENV"]);
        Assert.AreEqual("${secret:ACME_GH_TOKEN}", variables["GH_TOKEN"]);
        Assert.AreEqual("${var:GRAFANA_URL}", variables["GRAFANA_URL"]);
        Assert.AreEqual("~/.app/${env:USER}", variables["APP_SETTINGS"]);
        CollectionAssert.AreEquivalent(new[] { "NODE_ENV", "APP_SETTINGS" },
            _config.LoadProjectEnvironment(project, includeInherited: false).Select(static variable => variable.Key).ToArray());
    }

    [TestMethod]
    public void WritesOnlyTheLocalSettingAndDeletingAnOverrideRevealsTheInheritedSetting()
    {
        var repository = Path.Combine(_root, "repo");
        var project = Path.Combine(repository, "app");
        _options.InheritedProjectRoots = path => path == project ? [repository] : [];
        var upstream = WriteConfig(repository, "[env]\nNODE_ENV = \"production\"\n");
        const string own = "# project config\r\n[env] # environment\r\nNODE_ENV = \"test\" # override\r\n\r\n[other]\r\nthing = [ 1, 2 ]\r\n";
        var local = WriteConfig(project, own);

        Assert.IsTrue(_config.SaveProjectEnvironmentVariable(project, "node_env", "development"));
        Assert.AreEqual(own.Replace("\"test\"", "\"development\"", StringComparison.Ordinal), File.ReadAllText(local));
        Assert.IsTrue(_config.SaveProjectEnvironmentVariable(project, "GH_TOKEN", "${secret:ACME_GH_TOKEN}"));
        Assert.AreEqual("${secret:ACME_GH_TOKEN}", _config.LoadProjectEnvironment(project).ToDictionary()["GH_TOKEN"]);
        Assert.IsTrue(_config.SaveProjectEnvironmentVariable(project, "NODE_ENV", null));
        Assert.AreEqual("production", _config.LoadProjectEnvironment(project).ToDictionary()["NODE_ENV"]);
        Assert.IsFalse(_config.SaveProjectEnvironmentVariable(project, "NODE_ENV", null), "The inherited setting cannot be deleted from the child.");
        Assert.AreEqual("[env]\nNODE_ENV = \"production\"\n", File.ReadAllText(upstream));
    }

    [TestMethod]
    public void CreatesTheFileAndRefusesAPlainTokenOrMalformedReferenceWithoutEditingIt()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        Assert.IsTrue(_config.SaveProjectEnvironmentVariable(project, "GH_TOKEN", "${env:LOCAL_GH_TOKEN}"));
        var path = Path.Combine(project, ".alta", "config.toml");
        var written = File.ReadAllText(path);
        StringAssert.Contains(written, "[env]");
        StringAssert.Contains(Assert.ThrowsExactly<ArgumentException>(() =>
            _config.SaveProjectEnvironmentVariable(project, "GH_TOKEN", "not-a-real-token")).Message, "GH_TOKEN holds a secret");
        Assert.ThrowsExactly<ArgumentException>(() => _config.SaveProjectEnvironmentVariable(project, "ADDRESS", "${unknown:VALUE}"));
        Assert.ThrowsExactly<ArgumentException>(() => _config.SaveProjectEnvironmentVariable(project, "GIT_CONFIG_COUNT", "1"));
        Assert.AreEqual(written, File.ReadAllText(path));
    }

    private static string WriteConfig(string project, string text)
    {
        var folder = Directory.CreateDirectory(Path.Combine(project, ".alta")).FullName;
        var path = Path.Combine(folder, "config.toml");
        File.WriteAllText(path, text);
        return path;
    }
}
