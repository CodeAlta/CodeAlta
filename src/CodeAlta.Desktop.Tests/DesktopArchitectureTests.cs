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
            if (reference.Name!.StartsWith("XenoAtom", StringComparison.Ordinal))
                Assert.AreEqual("XenoAtom.Logging", reference.Name, "Desktop may log without referencing terminal presentation libraries.");
            if (reference.Name.StartsWith("CodeAlta", StringComparison.Ordinal))
                Assert.IsTrue(reference.Name is "CodeAlta.Catalog" or "CodeAlta.Agent" or "CodeAlta.Hosting" or "CodeAlta.Orchestration" or "CodeAlta.LiveTool" or "CodeAlta.Plugins" or "CodeAlta.Plugins.Abstractions" or "CodeAlta.Plugin.Mcp" or "CodeAlta.Plugin.Git" or "CodeAlta.Plugin.Statistics", reference.Name);
            Assert.AreNotEqual("altatui", reference.Name);
        }
    }

    [TestMethod]
    public void DesktopToolPackage_ShipsNoPrebuiltMacOSShim()
    {
        // The SDK does not sign the shims it packs, and macOS kills an unsigned arm64 executable when it starts.
        // Without a packaged shim, dotnet tool install creates and signs one on the user's machine.
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(SourceRoot, "CodeAlta", "CodeAlta.csproj"));
        var shims = project.Descendants("PackAsToolShimRuntimeIdentifiers").Single().Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CollectionAssert.AreEquivalent(new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64" }, shims);
    }
}
