using System.Xml.Linq;
using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopArchitectureTests
{
    internal static string SourceRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new AssertFailedException("Cannot find source root.");
        }
    }

    [TestMethod]
    public void DesktopAssembly_HasNoTerminalOrHostCompositionReferences()
    {
        var assembly = typeof(DesktopApplication).Assembly;
        Assert.AreEqual("alta", assembly.GetName().Name);
        foreach (var reference in assembly.GetReferencedAssemblies())
        {
            Assert.IsFalse(reference.Name!.StartsWith("XenoAtom", StringComparison.Ordinal));
            if (reference.Name.StartsWith("CodeAlta", StringComparison.Ordinal))
                Assert.IsTrue(reference.Name is "CodeAlta.Catalog" or "CodeAlta.Agent" or "CodeAlta.Hosting" or "CodeAlta.Orchestration" or "CodeAlta.Plugins" or "CodeAlta.Plugins.Abstractions", reference.Name);
            Assert.AreNotEqual("altatui", reference.Name);
        }
    }

    [TestMethod]
    public void DesktopAndTui_HaveSeparatePackageIdentityAndRidIntent()
    {
        var desktop = XDocument.Load(Path.Combine(SourceRoot, "CodeAlta", "CodeAlta.csproj"));
        var tui = XDocument.Load(Path.Combine(SourceRoot, "CodeAlta.Tui", "CodeAlta.Tui.csproj"));
        Assert.AreEqual("CodeAlta", desktop.Descendants("PackageId").Single().Value);
        Assert.AreEqual("alta", desktop.Descendants("ToolCommandName").Single().Value);
        Assert.AreEqual("Exe", desktop.Descendants("OutputType").Single().Value);
        var rids = desktop.Descendants("PackAsToolShimRuntimeIdentifiers").Single().Value.Split(';');
        CollectionAssert.AreEquivalent(new[] { "win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64" }, rids);
        Assert.AreEqual(8, tui.Descendants("RuntimeIdentifiers").Single().Value.Split(';').Length);
        Assert.AreEqual("altatui", tui.Descendants("ToolCommandName").Single().Value);
        CollectionAssert.AreEqual(new[] { "../CodeAlta.Catalog/CodeAlta.Catalog.csproj", "../CodeAlta.Agent/CodeAlta.Agent.csproj", "../CodeAlta.Hosting/CodeAlta.Hosting.csproj", "../CodeAlta.Orchestration/CodeAlta.Orchestration.csproj" },
            desktop.Descendants("ProjectReference").Select(value => value.Attribute("Include")!.Value).ToArray());
        var outputs = XDocument.Load(Path.Combine(SourceRoot, "CodeAlta", "Directory.Build.props"));
        Assert.IsEmpty(outputs.Descendants("BaseOutputPath"));
        Assert.IsEmpty(outputs.Descendants("BaseIntermediateOutputPath"));
    }

    [TestMethod]
    public void DesktopDefaultItems_ExcludeOldOutputsAndFrontendBuildArtifacts()
    {
        var outputs = XDocument.Load(Path.Combine(SourceRoot, "CodeAlta", "Directory.Build.props"));
        var excludes = outputs.Descendants("DefaultItemExcludes").Single().Value.Split(';');
        foreach (var path in new[] { "bin/**", "obj/**", "frontend/node_modules/**", "frontend/dist/**" })
        {
            CollectionAssert.Contains(excludes, path);
        }
    }

    [TestMethod]
    [Ignore("Brittle source-text inspection is not a functional desktop acceptance test.")]
    public void NativeLifecycle_KeepsDispatcherAliveAndResetsExitCodeOnDisposalFailure()
    {
        // Structural protection, not a substitute for opt-in real native lifecycle execution.
        var source = File.ReadAllText(Path.Combine(SourceRoot, "CodeAlta", "Desktop", "DesktopApplication.cs"));
        StringAssert.Contains(source, "ShutdownMode = NeoApplicationShutdownMode.Explicit");
        Assert.IsTrue(source.IndexOf("application.MainWindow = window", StringComparison.Ordinal) < source.IndexOf("await application.CreateEnvironmentAsync", StringComparison.Ordinal));
        StringAssert.Contains(source, "await using var binding");
        StringAssert.Contains(source, "await using var environment");
        StringAssert.Contains(source, "ExitCode = 1; // Also covers failures from asynchronous disposal");
        StringAssert.Contains(source, "application.ForceShutdown()");
    }
}
