using CodeAlta.Fleet.NodeApi;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.Tests;

/// <summary>The permission mode the sessions of a machine run in by default, as its profile keeps it in a file of its fleet folder.</summary>
[TestClass]
public sealed class FleetSessionDefaultsTests
{
    [TestMethod]
    public void AMachine_BypassesPermissionsUntilItsOwnerChooses_AndOnlyTwoModesAreKept()
    {
        var root = Directory.CreateTempSubdirectory("alta-session-defaults-").FullName;
        try
        {
            Assert.AreEqual(SessionPermissionModes.Bypass, FleetSessionDefaults.Read(root).PermissionMode, "a machine without the file bypasses permissions");
            Assert.IsFalse(FleetSessionDefaults.AsksByDefault(root));
            Assert.AreEqual(SessionPermissionModes.Ask, FleetSessionDefaults.Write(root, new FleetSessionDefaultsInfo("ASK")).PermissionMode);
            Assert.AreEqual(SessionPermissionModes.Ask, FleetSessionDefaults.Read(root).PermissionMode);
            Assert.IsTrue(FleetSessionDefaults.AsksByDefault(root));

            foreach (var bad in new[] { "acceptEdits", "auto", "", "plan" })
                Assert.ThrowsExactly<ArgumentException>(() => FleetSessionDefaults.Write(root, new FleetSessionDefaultsInfo(bad)), bad);
            Assert.AreEqual(SessionPermissionModes.Ask, FleetSessionDefaults.Read(root).PermissionMode, "a refused change writes nothing");

            // A file that does not read, or names another mode, is the default of a machine.
            File.WriteAllText(Path.Combine(root, FleetSessionDefaults.FileName), """{ "permission_mode": "auto" }""");
            Assert.AreEqual(SessionPermissionModes.Bypass, FleetSessionDefaults.Read(root).PermissionMode);
            File.WriteAllText(Path.Combine(root, FleetSessionDefaults.FileName), "{ broken");
            Assert.AreEqual(SessionPermissionModes.Bypass, FleetSessionDefaults.Read(root).PermissionMode);
            File.WriteAllText(Path.Combine(root, FleetSessionDefaults.FileName), """{ "permission_mode": "default" }""");
            Assert.AreEqual(SessionPermissionModes.Ask, FleetSessionDefaults.Read(root).PermissionMode, "the owner may write it by hand");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
