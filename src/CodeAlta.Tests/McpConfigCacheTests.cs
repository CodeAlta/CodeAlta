using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using ModelContextProtocol.Authentication;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public void ConfigCache_ReadsTheFilesAgainOnlyWhenOneOfThemChanged()
    {
        using var project = TempDirectory.Create();
        var config = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        WriteServers(config, "a");
        WriteProjectPolicy(project.Path, "[plugins.mcp.servers.a]\ndisabled_tools = [\"x\"]\n");
        var policyPath = McpPolicyWriter.GetProjectPolicyPath(project.Path);
        Settle(config, policyPath);
        var cache = new McpConfigCache();
        string[] Keys() => [.. cache.Discover(project.Path, _home.Path).EffectiveServers.Select(static server => server.Definition.Key)];
        McpPolicyOptions Policy() => cache.LoadPolicy(McpPolicyWriter.GetGlobalPolicyPath(_home.Path), policyPath);

        var first = cache.Discover(project.Path, _home.Path);
        Assert.AreSame(first, cache.Discover(project.Path, _home.Path), "Nothing changed: what was read answers.");
        Assert.AreEqual(1, cache.ConfigReads);
        Assert.AreSame(Policy(), Policy());
        Assert.AreEqual(1, cache.PolicyReads);

        // A file of CodeAlta that changed, one of another tool that appears or goes: read again at the next request.
        WriteServers(config, "a", "b");
        Settle(config);
        CollectionAssert.AreEqual(new[] { "a", "b" }, Keys());
        var shared = Path.Combine(project.Path, ".mcp.json");
        WriteServers(shared, "c");
        Settle(shared);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Keys());
        File.Delete(shared);
        CollectionAssert.AreEqual(new[] { "a", "b" }, Keys());
        Assert.AreEqual(4, cache.ConfigReads);

        // A file written a moment ago is read at each request: a write of the same size could keep its stamp.
        WriteServers(config, "d");
        CollectionAssert.AreEqual(new[] { "d" }, Keys());
        CollectionAssert.AreEqual(new[] { "d" }, Keys());
        Assert.AreEqual(6, cache.ConfigReads);
        Settle(config);
        Keys();
        Keys();
        Assert.AreEqual(7, cache.ConfigReads);

        // The same size and the same time, as a copy that keeps its time makes it: only an invalidation tells.
        var stamp = File.GetLastWriteTimeUtc(config);
        WriteServers(config, "e");
        File.SetLastWriteTimeUtc(config, stamp);
        CollectionAssert.AreEqual(new[] { "d" }, Keys());
        cache.Invalidate();
        CollectionAssert.AreEqual(new[] { "e" }, Keys());

        // A policy that cannot be parsed fails at each request, as before, and is read again once it is fixed.
        File.WriteAllText(policyPath, "[plugins.mcp\n");
        Settle(policyPath);
        var reads = cache.PolicyReads;
        Assert.ThrowsExactly<Tomlyn.TomlException>(() => Policy());
        Assert.ThrowsExactly<Tomlyn.TomlException>(() => Policy());
        Assert.AreEqual(reads + 2, cache.PolicyReads, "A file that fails is not kept.");
        File.WriteAllText(policyPath, "[plugins.mcp]\ntool_timeout_ms = 1234\n");
        Settle(policyPath);
        Assert.AreEqual(1234, Policy().ToolTimeoutMs);
        Assert.AreEqual(1234, Policy().ToolTimeoutMs);
        Assert.AreEqual(reads + 3, cache.PolicyReads);
    }

    [TestMethod]
    public void ConfigCache_GivesWhatADiscoveryOfTheSameFilesGives()
    {
        using var project = TempDirectory.Create();
        WriteServers(McpConfigDiscovery.GetGlobalConfigPath(_home.Path), "global", "shared");
        WriteServers(McpConfigDiscovery.GetProjectConfigPath(project.Path), "shared", "project");
        WriteServers(Path.Combine(project.Path, ".mcp.json"), "common");
        Directory.CreateDirectory(Path.Combine(project.Path, ".github"));
        File.WriteAllText(Path.Combine(project.Path, ".github", "mcp.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(project.Path, ".vscode"));
        File.WriteAllText(Path.Combine(project.Path, ".vscode", "mcp.json"), """{ "servers": { "asks": { "type": "stdio", "command": "x", "args": ["${input:token}"] }, "folder": { "type": "stdio", "command": "${workspaceFolder}/tool" } } }""");
        WriteServers(Path.Combine(_home.Path, ".copilot", "mcp-config.json"), "copilot");

        var expected = new McpConfigDiscovery().Discover(new McpConfigPathOptions { ProjectDirectory = project.Path, UserHomeDirectory = _home.Path, ProbeWritability = false, BoundedRead = false });
        var cached = new McpConfigCache().Discover(project.Path, _home.Path);

        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(cached));
        // The cache watches every file a discovery reads, in its order.
        CollectionAssert.AreEqual(
            expected.Sources.Concat(expected.ExternalSources).Select(static source => source.Path).ToArray(),
            McpConfigDiscovery.GetSourceFiles(Path.GetFullPath(project.Path), _home.Path).Select(static file => file.Path).ToArray());
        // The runtime reads the files of CodeAlta whole, as when it probed whether they could be written; it no longer probes.
        Assert.IsTrue(cached.Sources.All(static source => source.Exists && source.IsValid && !source.IsWritable));
    }

    [TestMethod]
    public async Task ThePrompt_ListsTheServersAManagementSnapshotListed()
    {
        using var project = TempDirectory.Create();
        WriteServers(McpConfigDiscovery.GetProjectConfigPath(project.Path), "a", "b", "c");
        var plugin = new McpPlugin(createPresentation: null, _home.Path);

        async Task<string?> PromptAsync(string? spaceId = null) => await plugin.GetSystemPromptContributions().Single().Content(new PluginSystemPromptContext
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            ProjectPath = project.Path,
            SpaceId = spaceId,
        }, CancellationToken.None);

        // What the prompt listed before: the configured servers of a management snapshot, the ones the policy does not turn off.
        string Expected(int maxServers)
        {
            var snapshot = new McpManagementService(_home.Path).RefreshSnapshot(new McpManagementRequest { ProjectDirectory = project.Path });
            var keys = snapshot.Servers.Where(static server => server.State == McpManagementServerState.Configured && server.PolicyEnabled != false)
                .Select(static server => server.Key).Order(StringComparer.Ordinal).ToArray();
            var shown = string.Join(", ", keys.Take(maxServers).Select(static key => $"`{key}`")) + (keys.Length > maxServers ? $", …(+{keys.Length - maxServers})" : "");
            return $"- Inactive (`alta mcp activate <id>*`): {shown}";
        }

        StringAssert.Contains(await PromptAsync(), Expected(10));
        StringAssert.Contains(await PromptAsync(), "`a`, `b`, `c`");

        WriteProjectPolicy(project.Path, "[plugins.mcp.servers.b]\nenabled = false\n");
        StringAssert.Contains(await PromptAsync(), Expected(10));
        StringAssert.Contains(await PromptAsync(), "): `a`, `c`");

        // MCP turned off, one server turned on by its own policy: that one is listed, as the snapshot lists it.
        WriteProjectPolicy(project.Path, "[plugins.mcp]\nenabled = false\n\n[plugins.mcp.servers.c]\nenabled = true\n");
        StringAssert.Contains(await PromptAsync(), Expected(10));
        StringAssert.Contains(await PromptAsync(), "): `c`");

        WriteProjectPolicy(project.Path, "[plugins.mcp]\nprompt_max_servers = 1\n");
        StringAssert.Contains(await PromptAsync(), Expected(1));
        StringAssert.Contains(await PromptAsync(), "): `a`, …(+2)");

        WriteProjectPolicy(project.Path, "[plugins.mcp]\ndiscover_in_prompt = false\n");
        Assert.IsNull(await PromptAsync());

        // A policy that cannot be read is the default one for the list, and fails a session of a space, as before.
        WriteProjectPolicy(project.Path, "[plugins.mcp\n");
        StringAssert.Contains(await PromptAsync(), Expected(10));
        StringAssert.Contains(await PromptAsync(), "): `a`, `b`, `c`");
        await Assert.ThrowsExactlyAsync<Tomlyn.TomlException>(async () => await PromptAsync("business"));
    }

    [TestMethod]
    public async Task OAuthTokenStatus_ReadSynchronously_IsTheStatusReadAsynchronously()
    {
        var path = Path.Combine(_home.Path, "tokens", "docs.json");
        var cache = new McpOAuthTokenCache(path);
        async Task AssertSameAsync(bool hasToken)
        {
            var expected = await cache.GetStatusAsync("docs", CancellationToken.None);
            Assert.AreEqual(expected, cache.GetStatus("docs"));
            Assert.AreEqual(hasToken, expected.HasToken);
        }

        await AssertSameAsync(hasToken: false);
        await cache.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-token-value",
            RefreshToken = "refresh-token-value",
            ExpiresIn = 3600,
            Scope = "read",
            ObtainedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        await AssertSameAsync(hasToken: true);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 13, 0, 0, TimeSpan.Zero), cache.GetStatus("docs").ExpiresAt);

        File.WriteAllText(path, """{ "access_token": " " }""");
        await AssertSameAsync(hasToken: false);
        File.WriteAllText(path, "{ not json");
        await AssertSameAsync(hasToken: false);
    }

    [TestMethod]
    public async Task TheToolCallsOfASession_DoNotReadTheConfigurationAgain_UntilAFileOfItChanges()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null);
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        try
        {
            await ActivateTinyAsync(plugin, project.Path, "session");
            var tools = await RunToolsAsync(plugin, project.Path, "session");
            Assert.AreEqual("echo:hello", await EchoAsync(tools, "session", "hello"));
            Settle(McpConfigDiscovery.GetProjectConfigPath(project.Path));
            Assert.AreEqual("echo:hello", await EchoAsync(tools, "session", "hello"));
            var (configReads, policyReads) = (plugin.ConfigCache.ConfigReads, plugin.ConfigCache.PolicyReads);

            // Each call read the MCP files and the policy again, and probed whether the files of CodeAlta could be written.
            const int Calls = 10;
            for (var call = 0; call < Calls; call++)
            {
                Assert.AreEqual("echo:hello", await EchoAsync(tools, "session", "hello"));
            }

            Assert.AreEqual(configReads, plugin.ConfigCache.ConfigReads, "No call read the MCP files again.");
            Assert.AreEqual(policyReads, plugin.ConfigCache.PolicyReads, "No call read the policy again.");

            // A policy written since is read by the next call, which applies it.
            WriteProjectPolicy(project.Path, "[plugins.mcp]\nmax_tool_output_chars = 6\n");
            Assert.AreEqual("echo:h\n[truncated]", await EchoAsync(tools, "session", "hello"));
            Assert.IsTrue(plugin.ConfigCache.PolicyReads > policyReads);
        }
        finally
        {
            await plugin.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheCommandsAndTheManagementOfThePlugin_InvalidateWhatItRead()
    {
        using var project = TempDirectory.Create();
        var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var contribution = plugin.GetAltaCommands().Single();
        async Task RunAsync(params string[] args)
        {
            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var app = new CommandApp("alta", "test") { contribution.CreateCommandNode(CreateAltaContext(stdout, stderr, project.Path)) };
            Assert.AreEqual(0, await app.RunAsync(["mcp", .. args], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stdout + stderr.ToString());
        }

        var config = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        var policyPath = McpPolicyWriter.GetProjectPolicyPath(project.Path);
        int ReadsAfter(Action action)
        {
            plugin.ConfigCache.Discover(project.Path, _home.Path);
            plugin.ConfigCache.LoadPolicy(McpPolicyWriter.GetGlobalPolicyPath(_home.Path), policyPath);
            var before = plugin.ConfigCache.ConfigReads + plugin.ConfigCache.PolicyReads;
            action();
            plugin.ConfigCache.Discover(project.Path, _home.Path);
            plugin.ConfigCache.LoadPolicy(McpPolicyWriter.GetGlobalPolicyPath(_home.Path), policyPath);
            return plugin.ConfigCache.ConfigReads + plugin.ConfigCache.PolicyReads - before;
        }

        await RunAsync("server", "add", "docs", "--url", "https://example.test/mcp");
        await RunAsync("server", "disable", "docs");
        Settle(config, policyPath);
        Assert.AreEqual(0, ReadsAfter(static () => { }), "Settled files are read once.");
        // A write of the plugin is read at the next request, whatever the stamps of the files.
        Assert.AreEqual(2, ReadsAfter(() => RunAsync("server", "enable", "docs").GetAwaiter().GetResult()));
        Settle(config, policyPath);
        Assert.AreEqual(2, ReadsAfter(() => RunAsync("server", "remove", "docs").GetAwaiter().GetResult()));
        Settle(config, policyPath);
        Assert.AreEqual(2, ReadsAfter(() => plugin.ManagementService.SetServerEnabledAsync("other", false, McpManagementScope.Project,
            new McpManagementRequest { ProjectDirectory = project.Path }).GetAwaiter().GetResult()));
        await plugin.DisposeAsync();
    }

    private static void WriteServers(string path, params string[] keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = keys.ToDictionary(static key => key, static key => new { command = "tool-" + key }) }));
    }

    // Written long enough ago for their stamps to tell the next write apart.
    private static void Settle(params string[] paths)
    {
        var time = DateTime.UtcNow - TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(Random.Shared.Next(1, 1_000_000));
        foreach (var path in paths)
        {
            File.SetLastWriteTimeUtc(path, time);
        }
    }
}
