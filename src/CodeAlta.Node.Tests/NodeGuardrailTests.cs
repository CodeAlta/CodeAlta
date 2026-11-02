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
