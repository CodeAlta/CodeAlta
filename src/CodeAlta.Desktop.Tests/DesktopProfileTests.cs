using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Desktop.Tests;

// A separate profile (--profile, CODEALTA_HOME), as altacli has it (#427).
[TestClass]
public sealed class DesktopProfileTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "alta-desktop-profile-fixture");

    private static DesktopLaunchOptions? Parse(string? home, params string[] args)
        => DesktopCommandLine.TryParse(args, _ => true, _ => false, home, out var options, out _) ? options : null;

    [TestMethod]
    public void Profile_KeepsTheConfigurationTheSessionsAndTheWindowDataInItsFolder()
    {
        var normal = Parse(null)!;
        var options = Parse(null, "--profile", Folder + Path.DirectorySeparatorChar);

        Assert.IsNotNull(options);
        Assert.AreEqual(Folder, options.Profile);
        Assert.AreEqual(Folder, options.CatalogRoot);
        Assert.IsNull(options.StateRoot);
        Assert.AreEqual(Path.Combine(Folder, "desktop"), options.DataRoot);
        Assert.IsFalse(options.Developer);
        // One entry of the desktop for the computer: the one of the normal instance.
        Assert.AreEqual(normal.DataRoot, options.IntegrationRoot);
        Assert.IsNull(normal.Profile);
        Assert.IsNull(normal.IntegrationRoot);
        Assert.AreEqual(normal.Owned, options.Owned);
        // Found apart from the normal instance, so that both run.
        Assert.AreNotEqual(DesktopApplication.InstanceId(normal), DesktopApplication.InstanceId(options));
    }

    [TestMethod]
    public void TheVariable_NamesTheProfileOfAStartThatNamesNone()
    {
        var other = Path.Combine(Path.GetTempPath(), "alta-desktop-profile-other");

        Assert.AreEqual(Folder, Parse(Folder)!.Profile);
        Assert.AreEqual(Folder, Parse(Folder, "--wait")!.Profile);
        Assert.AreEqual(other, Parse(Folder, "--profile", other)!.Profile, "--profile wins over CODEALTA_HOME");
        Assert.IsNull(Parse("  ")!.Profile, "an empty variable names no profile");
    }

    [TestMethod]
    public void Profile_GoesWithTheOtherOptionsOfANormalStart()
    {
        var project = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alta-desktop-profile-project"));
        var exit = Parse(null, "--profile", Folder, "--exit")!;
        Assert.IsTrue(exit.ExitRunning);
        Assert.AreEqual(Folder, exit.Profile);

        var folders = Parse(null, "--profile", Folder, project, "--space", "work")!;
        CollectionAssert.AreEqual(new[] { project }, folders.Projects.ToArray());
        CollectionAssert.AreEqual(new[] { "work" }, folders.Spaces.ToArray());
        Assert.AreEqual(project, folders.Owned!.Project);

        var mcp = Parse(null, "--profile", Folder, "--mcp-port", "0")!;
        Assert.AreEqual(Folder, mcp.Profile);
        Assert.AreEqual(0, mcp.McpPort);
    }

    [TestMethod]
    public void Profile_IsRefusedTwiceWithoutAFolderOnAFileOrWithDev()
    {
        Assert.IsNull(Parse(null, "--profile"));
        Assert.IsNull(Parse(null, "--profile", "--wait"));
        Assert.IsNull(Parse(null, "--profile", Folder, "--profile", Folder));
        Assert.IsNull(Parse(null, "--dev", "--profile", Folder));
        Assert.IsNull(Parse(Folder, "--dev"), "the developer instance is one of ~/.alta");
        Assert.IsFalse(DesktopCommandLine.TryParse(["--profile", Folder], _ => false, path => path == Folder, null, out _, out var error));
        StringAssert.Contains(error, "is a file");
    }

    [TestMethod]
    public void HandOver_GivesTheProfileToTheApplication()
    {
        CollectionAssert.AreEqual(new[] { "--profile", Folder }, DesktopTerminalStart.Arguments(Parse(null, "--profile", Folder)!).ToArray());
        // The variable reaches the application through the environment it inherits, and names the profile again there.
        CollectionAssert.AreEqual(new[] { "--profile", Folder }, DesktopTerminalStart.Arguments(Parse(Folder)!).ToArray());
    }

    [TestMethod]
    public void Profile_ReadsTheMcpFilesOfItsFolder_AndNothingOfTheHome()
    {
        var normal = Parse(null)!;
        Assert.IsTrue(normal.McpHome.IsDefault, "~/.alta keeps the MCP files of the user, as before");
        Assert.AreEqual(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), normal.OtherToolsHome);

        var options = Parse(null, "--profile", Folder)!;
        Assert.AreEqual(Folder, options.McpHome.Root);
        Assert.IsNull(options.McpHome.OtherToolsHome, "no MCP configuration of another tool of the home");
        Assert.IsNull(options.OtherToolsHome, "no skill, agent or instruction other tools keep in the home");
    }

    [TestMethod]
    public async Task Profile_TheMcpSettingsAndThePluginUseTheFilesOfTheProfile()
    {
        var profile = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "alta-desktop-profile-mcp-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(profile, "mcp.json"), """{"mcpServers":{"profile-tool":{"command":"profile-tool"}}}""");
            var options = Parse(null, "--profile", profile)!;

            // Settings › MCP servers: the global servers are those of the profile, and an edit writes its mcp.json.
            var service = new McpServersService(new ProjectCatalog(new CatalogOptions { GlobalRoot = profile }), "epoch", options.McpHome);
            var listed = await service.ListAsync(new("epoch", null), default);
            Assert.AreEqual("ok", listed.Status);
            CollectionAssert.AreEqual(new[] { "profile-tool" }, listed.Servers.Select(static server => server.Key).ToArray(),
                "nothing of ~/.alta/mcp.json, of the configurations of other tools or of this computer");
            var saved = await service.SaveAsync(new("epoch", null, "Global", null, null, new("added", "Stdio", "npx", [], null, null, [], null, true)), default);
            Assert.AreEqual("ok", saved.Status);
            StringAssert.Contains(File.ReadAllText(Path.Combine(profile, "mcp.json")), "\"added\"");

            // The plugin the sessions run.
            var definition = DesktopPlugins.BuiltInsFor(profile).Single(static plugin => plugin.Id == ComposerStatusService.McpPluginId);
            var plugin = (McpPlugin)definition.Factory();
            var snapshot = plugin.ManagementService.RefreshSnapshot(new McpManagementRequest { ProbeWritability = false });
            CollectionAssert.AreEquivalent(new[] { "added", "profile-tool" }, snapshot.Servers.Select(static server => server.Key).ToArray());
            Assert.AreSame(DesktopPlugins.BuiltIns, DesktopPlugins.BuiltInsFor(null), "~/.alta keeps the plugins as they were");
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [TestMethod]
    public void Help_NamesTheProfile()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, DesktopCommandLine.Run(["--help"], output, TextWriter.Null, _ => 1));
        StringAssert.Contains(output.ToString(), "--profile <folder>");
        StringAssert.Contains(output.ToString(), "CODEALTA_HOME");
    }
}
