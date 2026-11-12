using CodeAlta.Desktop;

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
    public void Help_NamesTheProfile()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, DesktopCommandLine.Run(["--help"], output, TextWriter.Null, _ => 1));
        StringAssert.Contains(output.ToString(), "--profile <folder>");
        StringAssert.Contains(output.ToString(), "CODEALTA_HOME");
    }
}
