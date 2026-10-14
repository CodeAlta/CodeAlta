using System.Globalization;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task McpServersOfAnotherSpace_AreNotOffered_NorActivated_NorRun_AndAProjectCannotWidenTheSpace()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        // The project lists another space for the server: only the global configuration assigns spaces.
        WriteProjectPolicy(project.Path, """
            [plugins.mcp]
            direct_exposure = "allowlist"

            [plugins.mcp.servers.tiny]
            direct_tools = ["echo"]
            allowed_tools = ["echo"]
            spaces = ["personal"]
            """);
        var globalPolicy = McpPolicyWriter.GetGlobalPolicyPath(_home.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(globalPolicy)!);
        File.WriteAllText(globalPolicy, "[plugins.mcp.servers.tiny]\nspaces = [\"business\"]\n");
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();

        async Task<(int ExitCode, string Stdout)> RunAsync(string? space, params string[] args)
        {
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var context = CreateAltaContext(stdout, stderr, project.Path, space is null ? null : "session-" + space) with
            {
                ResolveSourceSpaceIdAsync = _ => ValueTask.FromResult(space),
            };
            var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(context) };
            var exitCode = await app.RunAsync(["mcp", .. args], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
            return (exitCode, stdout.ToString());
        }

        async Task<string?> PromptAsync(string space)
            => await plugin.GetSystemPromptContributions().Single().Content(new PluginSystemPromptContext
            {
                Plugin = CreatePluginDescriptor(),
                Services = NoopPluginServices.Create(),
                ProjectPath = project.Path,
                SessionId = "session-" + space,
                SpaceId = space,
            }, CancellationToken.None);

        PluginBeforeAgentRunContext Run(string space, string session) => new()
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            ProjectPath = project.Path,
            SessionId = session,
            SpaceId = space,
        };

        // A session of another space is not offered the server, cannot activate it, nor turn it on.
        Assert.IsNull(await PromptAsync("personal"));
        StringAssert.Contains(await PromptAsync("business"), "`tiny`");
        var refused = await RunAsync("personal", "activate", "tiny");
        Assert.AreEqual(1, refused.ExitCode);
        using (var error = JsonDocument.Parse(refused.Stdout.Trim().Split('\n').Last()))
        {
            Assert.AreEqual("server_not_in_space", error.RootElement.GetProperty("code").GetString());
            StringAssert.Contains(error.RootElement.GetProperty("message").GetString(), "space 'personal'");
        }

        Assert.AreEqual("server_not_in_space", JsonDocument.Parse((await RunAsync("personal", "server", "enable", "tiny", "--global")).Stdout.Trim()).RootElement.GetProperty("code").GetString());
        Assert.IsFalse((await RunAsync("personal", "list")).Stdout.Contains("\"tiny\"", StringComparison.Ordinal));
        Assert.IsTrue((await RunAsync(null, "list")).Stdout.Contains("\"tiny\"", StringComparison.Ordinal), "The user lists every server.");

        // The runtime refuses it to a session of another space, whatever asks.
        await using (var runtime = new McpRuntimeService(_home.Path))
        {
            var tools = await runtime.ListToolsForServersAsync(new McpRuntimeRequest { ProjectDirectory = project.Path, SpaceId = "personal" }, ["tiny"], CancellationToken.None);
            Assert.AreEqual(0, tools.Tools.Count);
            Assert.AreEqual("server_not_in_space", tools.Diagnostics.Single().Code);
        }

        // Activated in its space, the server runs there; a session that is no longer in its space drops and forgets it.
        Assert.AreEqual(0, (await RunAsync("business", "activate", "tiny")).ExitCode);
        Assert.AreEqual("mcp__tiny__echo", (await plugin.OnBeforeAgentRunAsync(Run("business", "session-business"), CancellationToken.None))!.AdditionalTools.Single().Spec.Name);
        Assert.IsNull(await plugin.OnBeforeAgentRunAsync(Run("personal", "session-business"), CancellationToken.None));
        Assert.IsNull(await plugin.OnBeforeAgentRunAsync(Run("business", "session-business"), CancellationToken.None), "The activation was forgotten.");
    }

    [TestMethod]
    public async Task NoMcpCommand_WritesTheSpacesOfAServer()
    {
        using var project = TempDirectory.Create();
        var globalPolicy = McpPolicyWriter.GetGlobalPolicyPath(_home.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(globalPolicy)!);
        File.WriteAllText(globalPolicy, "[plugins.mcp.servers.docs]\nspaces = [\"business\", \"default\"]\n");
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        foreach (var args in new[]
                 {
                     new[] { "server", "add", "docs", "--url", "https://example.test/mcp", "--scope", "global" },
                     ["server", "disable", "docs", "--global"],
                     ["server", "enable", "docs", "--global"],
                     ["server", "add", "docs", "--url", "https://example.test/other", "--scope", "global"],
                     ["server", "remove", "docs", "--scope", "global"],
                 })
        {
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };
            Assert.AreEqual(0, await app.RunAsync(["mcp", .. args], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), string.Join(' ', args) + stdout + stderr);
            CollectionAssert.AreEqual(new[] { "business", "default" },
                SpaceAssignments.Parse(File.ReadAllText(globalPolicy)).Of(SpaceResourceKind.McpServer, "docs").ToArray(), string.Join(' ', args));
        }
    }

    [TestMethod]
    public void ThePluginReadsTheSpacesOfAServer_AsTheHostReadsThem()
    {
        const string Toml = """
            [plugins.mcp.servers.a]
            spaces = ["business", " business ", "default"]

            [plugins.mcp.servers.B]
            spaces = "personal"

            [plugins.mcp.servers.c]
            enabled = true
            """;
        var path = Path.Combine(_home.Path, "spaces-equivalence.toml");
        File.WriteAllText(path, Toml);
        var policy = new McpPolicyLoader().Load(path, null);
        var host = SpaceAssignments.Parse(Toml);
        foreach (var key in new[] { "a", "b", "c" })
        {
            foreach (var space in new[] { "business", "personal", SpaceDescriptor.DefaultId, "other" })
            {
                Assert.AreEqual(host.IsAvailable(SpaceResourceKind.McpServer, key, space), policy.IsServerInSpace(key, space), $"{key} in {space}");
            }
        }

        Assert.IsTrue(policy.IsServerInSpace("a", null), "No space applies none.");
    }
}
