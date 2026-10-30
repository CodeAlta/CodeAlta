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

    [TestMethod]
    public async Task Read_CountsForASessionTheServersOfItsSpaceOnly()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"},"search":{"command":"uvx"}}}""");
        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp.servers.docs]\nspaces = [\"work\"]\n");
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(fixture.Home, ".alta") });
        string? newSessionProject = "unset";
        var service = new ComposerStatusService(fixture.Projects, Epoch, fixture.Home)
        {
            SessionSpaceFilter = (sessionId, _) => Task.FromResult(SpaceFilter.Load(store, sessionId == "work-session" ? "work" : "personal")),
            NewSessionSpaceFilter = (projectId, _) => { newSessionProject = projectId; return Task.FromResult(SpaceFilter.Load(store, "personal")); },
        };

        var personal = await service.ReadAsync(new(Epoch, fixture.Project.Id, "personal-session"), default);
        var work = await service.ReadAsync(new(Epoch, fixture.Project.Id, "work-session"), default);
        var composer = await service.ReadAsync(new(Epoch, fixture.Project.Id), default);

        // A server of another space is neither counted nor offered to the session, nor to the one a composer without
        // a session will create, in the space the window shows.
        Assert.AreEqual("1/1 · tools not loaded", personal.Items.Single().Text);
        Assert.AreEqual("2/2 · tools not loaded", work.Items.Single().Text);
        Assert.AreEqual("1/1 · tools not loaded", composer.Items.Single().Text);
        Assert.AreEqual(fixture.Project.Id, newSessionProject);
        var chat = await service.ReadAsync(new(Epoch, null), default);
        Assert.AreEqual("1/1 · tools not loaded", chat.Items.Single().Text);
        Assert.IsNull(newSessionProject);
    }

    [TestMethod]
    public async Task ReadMcp_ListsTheServersOfTheSpaceWithTheirListedTools()
    {
        Assert.AreEqual("unavailable", (await new ComposerStatusService().ReadMcpAsync(new(Epoch, null), default)).Status);
        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadMcpAsync(new("other", null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Service.ReadMcpAsync(new(Epoch, null, "bad\nid"), default)).Status);
        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"},"search":{"command":"uvx"}}}""");
        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp.servers.search]\nenabled = false\n");
        var definition = new CodeAlta.Plugin.Mcp.McpConfigCache().Discover(fixture.Project.ProjectPath, new CodeAlta.Plugin.Mcp.McpHome(fixture.Home, null))
            .EffectiveServers.Single(static server => server.Definition.Key == "docs").Definition;
        new CodeAlta.Plugin.Mcp.McpToolListCache(CodeAlta.Plugin.Mcp.McpToolListCache.FolderOf(fixture.Home)).Write(definition,
        [
            new CodeAlta.Plugin.Mcp.McpRuntimeTool
            {
                Server = "docs", Name = "lookup", Alias = "mcp__docs__lookup", Description = "Looks up a page",
                InputSchema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"id":{"description":"The page"}}}""").RootElement.Clone(),
            },
        ]);

        var response = await fixture.Service.ReadMcpAsync(new(Epoch, fixture.Project.Id, "session-a"), default);

        Assert.AreEqual("ok", response.Status);
        Assert.IsTrue(response.Enabled);
        CollectionAssert.AreEqual(new[] { "docs:True", "search:False" }, response.Servers.Select(static server => $"{server.Key}:{server.Enabled}").ToArray());
        var docs = response.Servers[0];
        Assert.AreEqual("stdio", docs.Transport);
        Assert.AreEqual("global", docs.Scope);
        Assert.IsFalse(docs.InUse);
        var tool = docs.Tools.Single();
        Assert.AreEqual(new ComposerMcpArgument("id", "The page"), tool.Arguments.Single());
        Assert.AreEqual(("lookup", "Looks up a page", true, false), (tool.Name, tool.Description, tool.Enabled, tool.Active));
        Assert.AreEqual(0, response.Servers[1].Tools.Length);

        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp]\nenabled = false\n");
        var disabled = await fixture.Service.ReadMcpAsync(new(Epoch, null), default);
        Assert.AreEqual(("ok", false, 0), (disabled.Status, disabled.Enabled, disabled.Servers.Length));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _root = root;
            Projects = projects;
            Project = project;
            Service = new ComposerStatusService(projects, Epoch, Home);
        }

        public ProjectCatalog Projects { get; }

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
