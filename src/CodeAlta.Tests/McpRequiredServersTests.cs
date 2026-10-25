using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task RequiredServers_ThatStart_PassTheCheck_AndTheOthersAreNamedWithTheirReason()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(_home.Path, "tiny", logPath: null, global: true);
        WriteTinyServerConfig(project.Path, "off", logPath: null);
        WriteProjectPolicy(project.Path, """
            [plugins.mcp.servers.tiny]
            required = true

            [plugins.mcp.servers.off]
            required = true
            enabled = false

            [plugins.mcp.servers.ghost]
            required = true

            [plugins.mcp.servers.optional]
            required = false
            """);
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path);

        CollectionAssert.AreEqual(new[] { "ghost", "off", "tiny" }, plugin.GetRequiredServers(project.Path).ToArray());
        var failures = await plugin.CheckRequiredServersAsync(project.Path, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "ghost", "off" }, failures.Select(static failure => failure.Server).ToArray(), string.Join(Environment.NewLine, failures));
        Assert.AreEqual("server_not_found", failures[0].Code);
        Assert.AreEqual("server_disabled", failures[1].Code);
        Assert.AreEqual(0, (await plugin.CheckRequiredServersAsync(null, CancellationToken.None)).Count, "The user's configuration requires nothing.");
    }

    [TestMethod]
    public async Task GlobalConfigContent_IsThePolicyOfTheUser_InPlaceOfItsFile()
    {
        var globalPath = McpPolicyWriter.GetGlobalPolicyPath(_home.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
        File.WriteAllText(globalPath, "[plugins.mcp.servers.tiny]\nenabled = true\n");
        // What a host lays over the file (altacli -c plugins.mcp.servers.tiny.enabled=false) is what the plugin reads.
        var content = "[plugins.mcp.servers.tiny]\nenabled = false\nrequired = true\n";
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { GlobalConfigContent = () => content });

        Assert.IsFalse(plugin.ConfigCache.LoadPolicy(globalPath, null).Servers["tiny"].Enabled!.Value);
        CollectionAssert.AreEqual(new[] { "tiny" }, plugin.GetRequiredServers(null).ToArray());

        await using var fromFile = new McpPlugin(createPresentation: null, _home.Path);
        Assert.IsTrue(fromFile.ConfigCache.LoadPolicy(globalPath, null).Servers["tiny"].Enabled!.Value);
        Assert.AreEqual(0, fromFile.GetRequiredServers(null).Count);
    }
}
