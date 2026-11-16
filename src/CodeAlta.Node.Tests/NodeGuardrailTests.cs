namespace CodeAlta.Node.Tests;

[TestClass]
public sealed class NodeGuardrailTests
{
    [TestMethod]
    public void NodeApplication_IsReferencedByItsTestsAlone_AndReferencesNoOtherFrontend()
    {
        var sourceRoot = FindSourceRoot();
        var referencing = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(static file => Path.GetFileName(file) is not ("CodeAlta.Node.csproj" or "CodeAlta.Node.Tests.csproj"))
            .Where(static file => File.ReadAllText(file).Contains("CodeAlta.Node.csproj", StringComparison.Ordinal))
            .ToArray();
        Assert.IsEmpty(referencing, "Only the tests of CodeAlta Node reference it: " + string.Join(", ", referencing));

        var nodeProject = File.ReadAllText(Path.Combine(sourceRoot, "CodeAlta.Node", "CodeAlta.Node.csproj"));
        foreach (var frontend in new[] { "CodeAlta.csproj\"", "CodeAlta.Tui.csproj", "CodeAlta.Cli.csproj", "CodeAlta.Plugins.Tui.csproj", "XenoAtom.Terminal", "XenoAtom.CommandLine.Terminal", "XenoAtom.Logging.Terminal" })
        {
            Assert.IsFalse(nodeProject.Contains(frontend, StringComparison.Ordinal), $"CodeAlta.Node references {frontend}.");
        }
    }

    // The node serves the phone page with Kestrel and takes the page files from the one build of the frontend: the application
    // declares the ASP.NET Core framework (the SSH setup of the desktop checks it, doc/fleet/deployment.md) and its pack runs npm.
    [TestMethod]
    public void NodePackaging_DeclaresTheAspNetCoreRuntime_AndBuildsItsPhonePageWhereItIsPacked()
    {
        var sourceRoot = FindSourceRoot();
        var nodeProject = File.ReadAllText(Path.Combine(sourceRoot, "CodeAlta.Node", "CodeAlta.Node.csproj"));
        StringAssert.Contains(nodeProject, "<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", "the prerequisite is named by the project, not left to a transitive reference");
        StringAssert.Contains(nodeProject, "MobilePage.targets");

        // A pack of the node alone restores no desktop project: the page build restores it, and runs once for the builds of every runtime.
        var targets = File.ReadAllText(Path.Combine(sourceRoot, "CodeAlta.Plugin.Mobile", "MobilePage.targets"));
        StringAssert.Contains(targets, "Targets=\"Restore\"");
        StringAssert.Contains(targets, "RemoveProperties=\"RuntimeIdentifier\"");
        // The desktop is compiled for its frontend target as a build compiles it: with its embedded resources.
        Assert.IsFalse(targets.Contains("Targets=\"NeoAstraPrepareFrontend\"", StringComparison.Ordinal), "the frontend target is never asked alone");
        StringAssert.Contains(targets, "Targets=\"ResolveReferences;PrepareResources;NeoAstraPrepareFrontend\"");

        // The pack job of CI runs npm for the node, and checks that every package of it carries the page.
        var workflow = File.ReadAllText(Path.Combine(sourceRoot, "..", ".github", "workflows", "ci.yml"));
        StringAssert.Contains(workflow, "matrix.project == 'CodeAlta' || matrix.project == 'CodeAlta.Node'");
        StringAssert.Contains(workflow, "mobile-page/files.json");
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) return directory.FullName;
            if (File.Exists(Path.Combine(directory.FullName, "src", "CodeAlta.slnx"))) return Path.Combine(directory.FullName, "src");
        }

        Assert.Fail("Could not locate the source folder (CodeAlta.slnx) from the test output path.");
        return null!;
    }
}
