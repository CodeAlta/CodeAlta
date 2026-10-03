using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class McpServersRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new McpServersService();
        Assert.AreEqual("unavailable", (await unavailable.ListAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SaveAsync(new(Epoch, null, "Global", null, null, Stdio("docs")), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.RemoveAsync(new(Epoch, null, "Global", "docs"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SetEnabledAsync(new(Epoch, null, "Global", "docs", false), default)).Status);

        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("another", null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new(null, null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SaveAsync(new("another", null, "Global", null, null, Stdio("docs")), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.RemoveAsync(new("another", null, "Global", "docs"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SetEnabledAsync(new("another", null, "Global", "docs", false), default)).Status);
        Assert.IsFalse(File.Exists(fixture.GlobalJson), "Refused requests must not write.");
    }

    [TestMethod]
    public async Task List_ReturnsBothScopesWithPolicyAndNeverEchoesSecretValues()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, """
            {"mcpServers":{
              "shared":{"command":"global-tool"},
              "docs":{"command":"npx","args":["-y","--token","ARG_SECRET"],"cwd":"tools","env":{"API_TOKEN":"ENV_SECRET","MODE":"fast","EMPTY":""}},
              "https://KEY_SECRET.invalid/":{"command":"hidden"}}}
            """);
        File.WriteAllText(fixture.ProjectJson, """
            {"mcpServers":{
              "shared":{"command":"project-tool"},
              "remote":{"type":"http","url":"https://example.invalid/mcp?api_key=URL_SECRET","headers":{"Authorization":"Bearer HEADER_SECRET"}}}}
            """);
        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp.servers.docs]\nenabled = false\ndisabled_tools = [\"search\"]\n");

        var global = await fixture.Service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("ok", global.Status);
        Assert.AreEqual("ok", global.GlobalConfigState);
        Assert.IsNull(global.ProjectConfigState);
        Assert.IsTrue(global.McpEnabled);
        Assert.AreEqual(1, global.Omitted, "A key that is not an opaque identifier is counted, not listed.");
        CollectionAssert.AreEqual(new[] { "docs", "shared" }, global.Servers.Select(server => server.Key).ToArray());
        var docs = global.Servers[0];
        Assert.AreEqual("Global", docs.Scope);
        Assert.AreEqual("Stdio", docs.Transport);
        Assert.AreEqual("npx", docs.Command);
        Assert.AreEqual("tools", docs.WorkingDirectory);
        Assert.IsFalse(docs.Enabled);
        Assert.IsTrue(docs.ArgumentsRedacted);
        Assert.HasCount(3, docs.Arguments);
        Assert.AreEqual("--token", docs.Arguments[1]);
        CollectionAssert.AreEqual(new[] { "search" }, docs.DisabledTools.ToArray());
        CollectionAssert.AreEqual(new[] { "API_TOKEN", "EMPTY", "MODE" }, docs.Environment.Select(value => value.Name).ToArray());
        Assert.IsTrue(docs.Environment[0].HasValue);
        Assert.IsFalse(docs.Environment[1].HasValue);

        var scoped = await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default);
        Assert.AreEqual("ok", scoped.Status);
        Assert.AreEqual(fixture.Project.Id, scoped.ProjectId);
        Assert.AreEqual("ok", scoped.ProjectConfigState);
        Assert.HasCount(4, scoped.Servers);
        var overriding = scoped.Servers.Single(server => server is { Key: "shared", Scope: "Project" });
        Assert.IsTrue(overriding.OverridesGlobal);
        Assert.IsFalse(overriding.Shadowed);
        Assert.IsTrue(scoped.Servers.Single(server => server is { Key: "shared", Scope: "Global" }).Shadowed);
        var remote = scoped.Servers.Single(server => server.Key == "remote");
        Assert.AreEqual("Http", remote.Transport);
        Assert.IsTrue(remote.UrlRedacted);
        StringAssert.StartsWith(remote.Url, "https://example.invalid/mcp?api_key=");
        Assert.AreEqual("Authorization", remote.Headers.Single().Name);
        Assert.IsTrue(remote.Headers.Single().HasValue);

        var wire = JsonSerializer.Serialize(scoped, DesktopJsonContext.Default.McpServersListResponse);
        foreach (var secret in new[] { "ARG_SECRET", "ENV_SECRET", "URL_SECRET", "HEADER_SECRET", "KEY_SECRET", fixture.Home })
            Assert.IsFalse(wire.Contains(secret, StringComparison.Ordinal), secret);
        foreach (var directory in new[] { Path.GetDirectoryName(fixture.GlobalJson)!, fixture.ProjectPath })
            Assert.IsFalse(Directory.EnumerateFiles(directory).Any(file => Path.GetFileName(file).Contains("write-test", StringComparison.Ordinal)), "Listing must not probe writability.");
    }

    [TestMethod]
    public async Task Save_AddsAndUpdatesADefinition_KeepingStoredSecretsTheFormNeverReceived()
    {
        using var fixture = await Fixture.CreateAsync();
        var added = await fixture.Service.SaveAsync(new(Epoch, null, "Global", null, null, new("docs", "Stdio", "npx",
            ["-y", "--token", "ARG_SECRET"], null, null, [new("API_TOKEN", "ENV_SECRET"), new("MODE", "fast")], null, true)), default);
        Assert.AreEqual("ok", added.Status, added.Message);
        var stored = Server(fixture.GlobalJson, "docs");
        Assert.AreEqual("npx", (string?)stored["command"]);
        Assert.AreEqual("ENV_SECRET", (string?)stored["env"]!["API_TOKEN"]);
        Assert.IsFalse(File.Exists(fixture.GlobalPolicy), "An enabled server needs no policy entry.");

        var listed = (await fixture.Service.ListAsync(new(Epoch, null), default)).Servers.Single();
        Assert.IsTrue(listed.Enabled);
        Assert.IsTrue(listed.ArgumentsRedacted);
        Assert.IsFalse(listed.Arguments.Contains("ARG_SECRET"));

        // The form sends the redacted argument back untouched, keeps one variable and changes the other.
        var updated = await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", new("docs", "Stdio", "node",
            [.. listed.Arguments, "--verbose"], "tools", null, [new("API_TOKEN", null), new("MODE", "slow")], null, true)), default);
        Assert.AreEqual("ok", updated.Status, updated.Message);
        stored = Server(fixture.GlobalJson, "docs");
        Assert.AreEqual("node", (string?)stored["command"]);
        Assert.AreEqual("tools", (string?)stored["cwd"]);
        CollectionAssert.AreEqual(new[] { "-y", "--token", "ARG_SECRET", "--verbose" }, stored["args"]!.AsArray().Select(value => (string?)value).ToArray());
        Assert.AreEqual("ENV_SECRET", (string?)stored["env"]!["API_TOKEN"]);
        Assert.AreEqual("slow", (string?)stored["env"]!["MODE"]);

        var before = File.ReadAllText(fixture.GlobalJson);
        var refusals = new[]
        {
            await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", Stdio("docs") with { Environment = [new("NEW_TOKEN", null)] }), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", Stdio("bad key")), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", Stdio("docs") with { Transport = "Pipe" }), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", Stdio("docs") with { Command = " " }), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", Stdio("docs") with { Environment = [new("A", "1"), new("A", "2")] }), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Everywhere", "docs", "Global", Stdio("docs")), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Project", null, null, Stdio("local")), default),
            await fixture.Service.SaveAsync(new(Epoch, null, "Global", null, null, null), default),
        };
        foreach (var refusal in refusals)
        {
            Assert.AreEqual("invalid", refusal.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(refusal.Message));
            Assert.IsTrue(refusal.Message!.Length <= 512);
        }

        Assert.AreEqual("not_found", (await fixture.Service.SaveAsync(new(Epoch, null, "Global", "ghost", "Global", Stdio("ghost")), default)).Status);
        Assert.AreEqual("conflict", (await fixture.Service.SaveAsync(new(Epoch, null, "Global", null, null, Stdio("docs")), default)).Status,
            "Adding a server must not replace the one that already uses the key.");
        Assert.AreEqual(before, File.ReadAllText(fixture.GlobalJson), "Refused saves must not write.");
    }

    [TestMethod]
    public async Task Save_RenamesMovesBetweenScopesAndWritesEnablementPolicy()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var remote = new McpServerEdit("remote", "Http", null, null, null, "https://example.invalid/mcp?api_key=URL_SECRET", null,
            [new("Authorization", "Bearer HEADER_SECRET")], false);
        Assert.AreEqual("ok", (await fixture.Service.SaveAsync(new(Epoch, project, "Project", null, null, remote), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.SaveAsync(new(Epoch, project, "Global", null, null, Stdio("other")), default)).Status);
        Assert.AreEqual("https://example.invalid/mcp?api_key=URL_SECRET", (string?)Server(fixture.ProjectJson, "remote")["url"]);
        StringAssert.Contains(File.ReadAllText(fixture.ProjectPolicy), "remote", "A disabled server is written to the policy of its scope.");

        var listed = (await fixture.Service.ListAsync(new(Epoch, project), default)).Servers.Single(server => server.Key == "remote");
        Assert.IsFalse(listed.Enabled);
        Assert.IsTrue(listed.UrlRedacted);

        // Renaming with the redacted URL and a kept header leaves both secrets in the file.
        var renamed = await fixture.Service.SaveAsync(new(Epoch, project, "Project", "remote", "Project",
            remote with { Key = "renamed", Url = listed.Url, Headers = [new("Authorization", null)], Enabled = true }), default);
        Assert.AreEqual("ok", renamed.Status, renamed.Message);
        Assert.IsNull(Servers(fixture.ProjectJson)["remote"]);
        var stored = Server(fixture.ProjectJson, "renamed");
        Assert.AreEqual("https://example.invalid/mcp?api_key=URL_SECRET", (string?)stored["url"]);
        Assert.AreEqual("Bearer HEADER_SECRET", (string?)stored["headers"]!["Authorization"]);

        Assert.AreEqual("conflict", (await fixture.Service.SaveAsync(new(Epoch, project, "Global", "renamed", "Project",
            remote with { Key = "other", Url = listed.Url, Headers = [] }), default)).Status, "Moving must not replace another definition.");

        var moved = await fixture.Service.SaveAsync(new(Epoch, project, "Global", "renamed", "Project",
            remote with { Key = "renamed", Url = listed.Url, Headers = [new("Authorization", null)], Enabled = true }), default);
        Assert.AreEqual("ok", moved.Status, moved.Message);
        Assert.IsNull(Servers(fixture.ProjectJson)["renamed"]);
        Assert.AreEqual("Bearer HEADER_SECRET", (string?)Server(fixture.GlobalJson, "renamed")["headers"]!["Authorization"]);
        var after = await fixture.Service.ListAsync(new(Epoch, project), default);
        CollectionAssert.AreEqual(new[] { "other", "renamed" }, after.Servers.Select(server => server.Key).ToArray());
        Assert.IsTrue(after.Servers.All(server => server is { Scope: "Global", Enabled: true }));
    }

    [TestMethod]
    public async Task RemoveAndSetEnabled_ChangeOnlyTheNamedDefinition()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"},"other":{"command":"node"}}}""");

        Assert.AreEqual("not_found", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "ghost", false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "bad key", false), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Project", "docs", false), default)).Status);
        Assert.IsFalse(File.Exists(fixture.GlobalPolicy));

        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "docs", false), default)).Status);
        var listed = await fixture.Service.ListAsync(new(Epoch, null), default);
        Assert.IsFalse(listed.Servers.Single(server => server.Key == "docs").Enabled);
        Assert.IsTrue(listed.Servers.Single(server => server.Key == "other").Enabled);
        Assert.AreEqual("ok", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "docs", true), default)).Status);
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, null), default)).Servers.All(server => server.Enabled));

        Assert.AreEqual("not_found", (await fixture.Service.RemoveAsync(new(Epoch, null, "Global", "ghost"), default)).Status);
        Assert.AreEqual("ok", (await fixture.Service.RemoveAsync(new(Epoch, null, "Global", "docs"), default)).Status);
        Assert.IsNull(Servers(fixture.GlobalJson)["docs"]);
        Assert.IsNotNull(Servers(fixture.GlobalJson)["other"]);
        Assert.AreEqual("other", (await fixture.Service.ListAsync(new(Epoch, null), default)).Servers.Single().Key);
        Assert.AreEqual("not_found", (await fixture.Service.RemoveAsync(new(Epoch, null, "Global", "docs"), default)).Status);
    }

    [TestMethod]
    public async Task UnreadableConfiguration_IsReportedAndLeftUntouched()
    {
        using var fixture = await Fixture.CreateAsync();
        File.WriteAllText(fixture.GlobalJson, "{ broken SECRET_CONFIG");
        var listed = await fixture.Service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("ok", listed.Status);
        Assert.AreEqual("invalid", listed.GlobalConfigState);
        Assert.IsEmpty(listed.Servers);
        var refused = await fixture.Service.SaveAsync(new(Epoch, null, "Global", null, null, Stdio("docs")), default);
        Assert.AreEqual("config_invalid", refused.Status);
        Assert.IsNull(refused.Message);
        Assert.AreEqual("config_invalid", (await fixture.Service.RemoveAsync(new(Epoch, null, "Global", "docs"), default)).Status);
        Assert.AreEqual("{ broken SECRET_CONFIG", File.ReadAllText(fixture.GlobalJson));

        File.WriteAllText(fixture.GlobalJson, """{"mcpServers":{"docs":{"command":"npx"}}}""");
        File.WriteAllText(fixture.GlobalPolicy, "[plugins.mcp\nSECRET_POLICY");
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, null), default)).PolicyReadError);
        Assert.AreEqual("config_invalid", (await fixture.Service.SetEnabledAsync(new(Epoch, null, "Global", "docs", false), default)).Status);
        Assert.AreEqual("policy_failed", (await fixture.Service.SaveAsync(new(Epoch, null, "Global", "docs", "Global", Stdio("docs") with { Enabled = false }), default)).Status,
            "The definition is saved even though its enablement cannot be written.");
        Assert.AreEqual("[plugins.mcp\nSECRET_POLICY", File.ReadAllText(fixture.GlobalPolicy));
    }

    [TestMethod]
    public async Task ProjectScope_IsResolvedThroughTheCatalogAndRefusedWhenUnknownArchivedOrGone()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, Guid.NewGuid().ToString("D")), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, fixture.ProjectPath), default)).Status, "A path is not a project id.");
        Assert.AreEqual("unknown_project", (await fixture.Service.SaveAsync(new(Epoch, "", "Project", null, null, Stdio("docs")), default)).Status);

        var gone = Directory.CreateDirectory(Path.Combine(fixture.ProjectPath, "..", "gone")).FullName;
        var removed = await fixture.Projects.UpsertFromPathAsync(gone);
        Directory.Delete(gone);
        Assert.AreEqual("project_unavailable", (await fixture.Service.ListAsync(new(Epoch, removed.Id), default)).Status);
        Assert.AreEqual("project_unavailable", (await fixture.Service.SaveAsync(new(Epoch, removed.Id, "Project", null, null, Stdio("docs")), default)).Status);
        Assert.IsFalse(Directory.Exists(gone), "A refused save must not recreate the project folder.");

        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual("archived_project", (await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.SaveAsync(new(Epoch, fixture.Project.Id, "Project", null, null, Stdio("docs")), default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.RemoveAsync(new(Epoch, fixture.Project.Id, "Project", "docs"), default)).Status);
        Assert.IsFalse(File.Exists(fixture.ProjectJson));
    }

    private static McpServerEdit Stdio(string key) => new(key, "Stdio", "npx", [], null, null, [], null, true);

    private static JsonObject Servers(string path) => JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();

    private static JsonObject Server(string path, string key) => Servers(path)[key]!.AsObject();

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _root = root;
            Projects = projects;
            Project = project;
            Service = new McpServersService(projects, Epoch, Home);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-mcp-servers-" + Guid.NewGuid().ToString("N"));
            var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project", ".alta")).Parent!.FullName);
            return new Fixture(root, projects, project);
        }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public McpServersService Service { get; }
        public string Home => Path.Combine(_root, "home");
        public string ProjectPath => Project.ProjectPath;
        public string GlobalJson => Path.Combine(Home, ".alta", "mcp.json");
        public string GlobalPolicy => Path.Combine(Home, ".alta", "config.toml");
        public string ProjectJson => Path.Combine(ProjectPath, ".alta", "mcp.json");
        public string ProjectPolicy => Path.Combine(ProjectPath, ".alta", "config.toml");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
