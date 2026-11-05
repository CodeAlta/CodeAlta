using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>The labels of a machine as its profile keeps them: names, each once whatever its case, in a file of its fleet folder.</summary>
[TestClass]
public sealed class FleetMachineLabelsTests
{
    [TestMethod]
    public void Labels_AreKeptOnceInTheirOrder_AndOnlyNamesAreLabels()
    {
        var root = Directory.CreateTempSubdirectory("alta-labels-").FullName;
        try
        {
            Assert.AreEqual(0, FleetMachineLabels.Read(root).Count, "a machine without the file has no label");
            CollectionAssert.AreEqual(new[] { "gpu", "prod", "os:linux" }, FleetMachineLabels.Write(root, [" gpu", "prod", "GPU", "os:linux"]).ToArray());
            CollectionAssert.AreEqual(new[] { "gpu", "prod", "os:linux" }, FleetMachineLabels.Read(root).ToArray());

            foreach (var bad in new[] { "two words", "", "a/b", new string('x', FleetMachineLabels.MaxLength + 1) })
                Assert.ThrowsExactly<ArgumentException>(() => FleetMachineLabels.Write(root, [bad]), bad);
            Assert.ThrowsExactly<ArgumentException>(() => FleetMachineLabels.Write(root, Enumerable.Range(0, FleetMachineLabels.MaxLabels + 1).Select(static index => $"l{index}")));
            CollectionAssert.AreEqual(new[] { "gpu", "prod", "os:linux" }, FleetMachineLabels.Read(root).ToArray(), "a refused change writes nothing");

            // A label written by hand that is not one is passed over; a file that does not read gives none.
            File.WriteAllText(Path.Combine(root, FleetMachineLabels.FileName), """["ok", "not ok", "OK"]""");
            CollectionAssert.AreEqual(new[] { "ok" }, FleetMachineLabels.Read(root).ToArray());
            File.WriteAllText(Path.Combine(root, FleetMachineLabels.FileName), "{ broken");
            Assert.AreEqual(0, FleetMachineLabels.Read(root).Count);
            Assert.AreEqual(0, FleetMachineLabels.Write(root, []).Count, "set alone removes them all");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
