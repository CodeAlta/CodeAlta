using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PluginsRpcTests
{
    private const string Epoch = "epoch-1";

    private const string GlobalConfiguration = """
        [skills]
        disabled = ["one"]

        [plugins.mcp]
        enabled = false
        tool_timeout_ms = 1234

        [plugins.mcp.servers.docs]
        enabled = false
        disabled_tools = ["search"]
        """;

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new PluginsService();
        Assert.AreEqual("unavailable", (await unavailable.ListAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SetEnabledAsync(new(Epoch, null, "Global", "sample-plugin", false), default)).Status);

        using var fixture = await Fixture.CreateAsync(GlobalConfiguration);
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("another", null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SetEnabledAsync(new(null, null, "Global", "sample-plugin", false), default)).Status);
        Assert.AreEqual(GlobalConfiguration, File.ReadAllText(fixture.GlobalConfig), "Refused requests must not write.");
    }

    [TestMethod]
    public async Task List_ReturnsDiscoveredSourcePackagesAndConfiguredIds()
    {
        using var fixture = await Fixture.CreateAsync(GlobalConfiguration);
        var global = await fixture.Service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("ok", global.Status);
        Assert.AreEqual(0, global.Omitted);
        CollectionAssert.AreEquivalent(new[] { "sample-plugin", "mcp" }, global.Plugins.Select(plugin => plugin.Id).ToArray());
        var sample = global.Plugins.Single(plugin => plugin.Id == "sample-plugin");
        Assert.AreEqual("Source", sample.Kind);
        Assert.AreEqual("Global", sample.Scope);
        Assert.AreEqual("Sample plugin", sample.Description);
        Assert.AreEqual("Enabled", sample.State);
        Assert.IsTrue(sample is { Enabled: true, EnabledGlobal: null, EnabledProject: null });
        var configured = global.Plugins.Single(plugin => plugin.Id == "mcp");
        Assert.AreEqual("Config", configured.Kind);
        Assert.AreEqual("Disabled", configured.State);
        Assert.IsTrue(configured is { Enabled: false, EnabledGlobal: false, EnabledProject: null });

        var scoped = await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default);
        Assert.AreEqual(fixture.Project.Id, scoped.ProjectId);
        Assert.HasCount(3, scoped.Plugins);
        var local = scoped.Plugins.Single(plugin => plugin.Id == "local-plugin");
        Assert.AreEqual("Source", local.Kind);
        Assert.AreEqual("Project", local.Scope);
        Assert.IsNull(local.Description, "A package without a README has no description.");
        Assert.IsFalse(scoped.Plugins.Any(plugin => (plugin.Description ?? plugin.Name).Contains(fixture.ProjectPath, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SetEnabled_WritesTheOverrideAndKeepsEveryOtherSetting()
    {
        using var fixture = await Fixture.CreateAsync(GlobalConfiguration);
        var project = fixture.Project.Id;
        var disabled = await fixture.Service.SetEnabledAsync(new(Epoch, project, "Global", "sample-plugin", false), default);
        Assert.AreEqual("ok", disabled.Status, disabled.Message);
        var store = new CodeAltaConfigStore(fixture.Projects.Options);
        Assert.AreEqual(false, store.LoadGlobal().Plugins!["sample-plugin"].Enabled);
        var text = File.ReadAllText(fixture.GlobalConfig);
        foreach (var kept in new[] { "tool_timeout_ms = 1234", "docs", "disabled_tools", "search", "\"one\"" })
            StringAssert.Contains(text, kept, "Settings the toggle does not own are preserved.");
        var sample = (await fixture.Service.ListAsync(new(Epoch, project), default)).Plugins.Single(plugin => plugin.Id == "sample-plugin");
        Assert.AreEqual("Disabled", sample.State);
        Assert.IsTrue(sample is { Enabled: false, EnabledGlobal: false, EnabledProject: null });
        Assert.IsFalse(File.Exists(fixture.ProjectConfig));

        // An id only named in configuration, and one the desktop cannot list at all (a built-in), can be set too.
        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Global", "mcp", true), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Global", "github", false), default)).Status);
        var listed = await fixture.Service.ListAsync(new(Epoch, project), default);
        Assert.IsTrue(listed.Plugins.Single(plugin => plugin.Id == "mcp") is { Enabled: true, EnabledGlobal: true, State: "Configured" });
        Assert.IsTrue(listed.Plugins.Single(plugin => plugin.Id == "github") is { Kind: "Config", Enabled: false, State: "Disabled" });
        StringAssert.Contains(File.ReadAllText(fixture.GlobalConfig), "tool_timeout_ms = 1234");

        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Project", "local-plugin", false), default)).Status);
        Assert.AreEqual(false, store.LoadProject(fixture.ProjectPath).Plugins!["local-plugin"].Enabled);
        var local = (await fixture.Service.ListAsync(new(Epoch, project), default)).Plugins.Single(plugin => plugin.Id == "local-plugin");
        Assert.IsTrue(local is { Enabled: false, EnabledGlobal: null, EnabledProject: false, State: "Disabled" });
        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, project, "Project", "local-plugin", true), default)).Status);
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, project), default)).Plugins.Single(plugin => plugin.Id == "local-plugin") is { Enabled: true, EnabledProject: true });
    }

    [TestMethod]
    public async Task SetEnabled_OnAMissingGlobalFileStartsFromTheBundledTemplate()
    {
        using var fixture = await Fixture.CreateAsync(null);
        using var reference = await Fixture.CreateAsync(null);
        var template = new CodeAltaConfigStore(reference.Projects.Options);
        template.EnsureGlobalConfigExists();

        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "sample-plugin", false), default)).Status);
        Assert.IsTrue(CodeAltaConfigStore.ValidateGlobalConfigContent(File.ReadAllText(fixture.GlobalConfig)).IsValid);
        var store = new CodeAltaConfigStore(fixture.Projects.Options);
        Assert.AreEqual(false, store.LoadGlobal().Plugins!["sample-plugin"].Enabled);
        CollectionAssert.AreEquivalent(
            template.LoadGlobalProviderDefinitions(includeDisabled: true).Select(provider => provider.ProviderKey).ToArray(),
            store.LoadGlobalProviderDefinitions(includeDisabled: true).Select(provider => provider.ProviderKey).ToArray(),
            "The providers of the template the editor shows for a missing file are not lost.");
    }

    [TestMethod]
    public async Task InvalidRequestsUnknownProjectsAndUnreadableConfiguration_WriteNothing()
    {
        using var fixture = await Fixture.CreateAsync(GlobalConfiguration);
        foreach (var request in new PluginsSetEnabledRequest[]
        {
            new(Epoch, null, "Global", "../escape", false), new(Epoch, null, "Global", "", false), new(Epoch, null, "Global", null, false),
            new(Epoch, null, "Global", "has space", false), new(Epoch, null, "Global", new string('a', 129), false),
            new(Epoch, null, "Everywhere", "sample-plugin", false), new(Epoch, null, "Project", "sample-plugin", false),
        })
        {
            var refused = await fixture.Service.SetEnabledAsync(request, default);
            Assert.AreEqual("invalid", refused.Status, request.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(refused.Message));
        }

        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, Guid.NewGuid().ToString("D")), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.SetEnabledAsync(new(Epoch, fixture.ProjectPath, "Project", "local-plugin", false), default)).Status, "A path is not a project id.");
        Assert.AreEqual(GlobalConfiguration, File.ReadAllText(fixture.GlobalConfig));

        File.WriteAllText(fixture.GlobalConfig, "[plugins\nbroken");
        Assert.AreEqual("config_invalid", (await fixture.Service.ListAsync(new(Epoch, null), default)).Status);
        var unreadable = await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "sample-plugin", false), default);
        Assert.AreEqual("config_invalid", unreadable.Status);
        Assert.IsNull(unreadable.Message);
        Assert.AreEqual("[plugins\nbroken", File.ReadAllText(fixture.GlobalConfig));
        File.WriteAllText(fixture.GlobalConfig, "plugins = 1\n");
        Assert.AreEqual("config_invalid", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "sample-plugin", false), default)).Status);
        Assert.AreEqual("plugins = 1\n", File.ReadAllText(fixture.GlobalConfig));
        File.WriteAllText(fixture.GlobalConfig, GlobalConfiguration);

        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual("archived_project", (await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.SetEnabledAsync(new(Epoch, fixture.Project.Id, "Project", "local-plugin", false), default)).Status);
        Assert.IsFalse(File.Exists(fixture.ProjectConfig));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project, string? configuration)
        {
            _root = root;
            Projects = projects;
            Project = project;
            if (configuration is not null) File.WriteAllText(GlobalConfig, configuration);
            var sample = Directory.CreateDirectory(Path.Combine(projects.Options.GlobalRoot, "plugins", "sample-plugin")).FullName;
            File.WriteAllText(Path.Combine(sample, "plugin.cs"), "// A source plugin entry file; the desktop never builds it.\n");
            File.WriteAllText(Path.Combine(sample, "README.md"), "\n# Sample plugin\n\nMore text.\n");
            var local = Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta", "plugins", "local-plugin")).FullName;
            File.WriteAllText(Path.Combine(local, "plugin.cs"), "// A project source plugin entry file.\n");
            Service = new PluginsService(projects, Epoch);
        }

        public static async Task<Fixture> CreateAsync(string? configuration)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-plugins-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project, configuration);
        }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public PluginsService Service { get; }
        public string ProjectPath => Project.ProjectPath;
        public string GlobalConfig => Projects.Options.ConfigPath;
        public string ProjectConfig => Path.Combine(ProjectPath, ".alta", "config.toml");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
