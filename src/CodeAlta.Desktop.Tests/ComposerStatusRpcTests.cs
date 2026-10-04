using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ComposerStatusRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task Read_RefusesUnavailableStaleAndUnknownProjectRequests()
    {
        Assert.AreEqual("unavailable", (await new ComposerStatusService().ReadAsync(new(Epoch, null), default)).Status);

        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"}}}""");
        var stale = await fixture.Service.ReadAsync(new("other", null), default);
        Assert.AreEqual("stale_epoch", stale.Status);
        Assert.AreEqual(0, stale.Items.Length);
        Assert.AreEqual("unknown_project", (await fixture.Service.ReadAsync(new(Epoch, "missing"), default)).Status);
    }

    [TestMethod]
    public async Task Read_WithoutMcpConfiguration_ReturnsNoItem()
    {
        using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.ReadAsync(new(Epoch, fixture.Project.Id), default);

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual(fixture.Project.Id, response.ProjectId);
        Assert.AreEqual(0, response.Items.Length);
    }

    [TestMethod]
    public async Task Read_CountsTheServersOfTheGlobalAndProjectConfiguration()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"},"search":{"command":"uvx"}}}""");
        File.WriteAllText(fixture.ProjectJson, """{"mcpServers":{"local":{"command":"node"}}}""");

        var global = await fixture.Service.ReadAsync(new(Epoch, null), default);
        var project = await fixture.Service.ReadAsync(new(Epoch, fixture.Project.Id), default);

        Assert.AreEqual(new ComposerStatusItem("mcp", "mcp-status", "MCP", "2/2 · tools not loaded", "info", "mcp"), global.Items.Single());
        Assert.AreEqual(new ComposerStatusItem("mcp", "mcp-status", "MCP", "3/3 · tools not loaded", "info", "mcp"), project.Items.Single());
    }

    [TestMethod]
    public async Task Read_WarnsAboutDisabledServersAndHidesTheItemWhenMcpIsDisabled()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"},"search":{"command":"uvx"}}}""");
        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp.servers.docs]\nenabled = false\n");

        var item = (await fixture.Service.ReadAsync(new(Epoch, null), default)).Items.Single();

        Assert.AreEqual("warning", item.Tone);
        Assert.AreEqual("1/2 · 1 unavailable · tools not loaded", item.Text);

        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp]\nenabled = false\n");
        var disabled = await fixture.Service.ReadAsync(new(Epoch, null), default);
        Assert.AreEqual("ok", disabled.Status);
        Assert.AreEqual(0, disabled.Items.Length);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _root = root;
            Project = project;
            Service = new ComposerStatusService(projects, Epoch, Home);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-composer-status-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project", ".alta")).Parent!.FullName);
            return new Fixture(root, projects, project);
        }

        public ProjectDescriptor Project { get; }
        public ComposerStatusService Service { get; }
        public string Home => Path.Combine(_root, "home");
        public string GlobalJson => Path.Combine(Home, ".alta", "mcp.json");
        public string GlobalPolicy => Path.Combine(Home, ".alta", "config.toml");
        public string ProjectJson => Path.Combine(Project.ProjectPath, ".alta", "mcp.json");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
