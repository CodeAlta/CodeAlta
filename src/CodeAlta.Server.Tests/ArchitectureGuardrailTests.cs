using System.Text.Json;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Server.Tests;

/// <summary>
/// The server references the fleet protocol and the self-update only, never the agent runtime
/// (<c>doc/fleet/architecture.md</c>): it cannot run an agent or read a journal by accident. The self-update references
/// the protocol alone, so that it brings nothing else in.
/// </summary>
[TestClass]
public sealed class ArchitectureGuardrailTests
{
    [TestMethod]
    public void TheServerAssemblyReferencesNoCodeAltaAssemblyButTheProtocolAndTheSelfUpdate()
    {
        var references = typeof(CoordinatorServer).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name!)
            .Where(static name => name.StartsWith("CodeAlta", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { "CodeAlta.Fleet.Protocol", "CodeAlta.Fleet.Update" }, references);
    }

    [TestMethod]
    public void TheSelfUpdateReferencesNoCodeAltaAssemblyButTheProtocol()
    {
        var references = typeof(CodeAlta.Fleet.Update.UpdateLauncher).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name!)
            .Where(static name => name.StartsWith("CodeAlta", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { "CodeAlta.Fleet.Protocol" }, references);
    }

    [TestMethod]
    public void TheDependencyGraphOfTheServerHoldsNoCodeAltaLibraryButTheProtocolAndTheSelfUpdate()
    {
        // The graph the build wrote for altaserver: a project reference shows here even when no type of it is used.
        var depsFile = Path.ChangeExtension(typeof(CoordinatorServer).Assembly.Location, ".deps.json");
        Assert.IsTrue(File.Exists(depsFile), depsFile);
        using var document = JsonDocument.Parse(File.ReadAllText(depsFile));
        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(static library => library.Name.Split('/')[0])
            .Where(static name => name.StartsWith("CodeAlta", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "CodeAlta.Fleet.Protocol", "CodeAlta.Fleet.Update" }, libraries);
    }

    [TestMethod]
    public void TheProtocolReferencesNoCodeAltaAssembly()
    {
        var references = typeof(CodeAlta.Fleet.Protocol.FleetConnection).Assembly.GetReferencedAssemblies()
            .Where(static name => name.Name!.StartsWith("CodeAlta", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.AreEqual(0, references.Length);
    }
}
