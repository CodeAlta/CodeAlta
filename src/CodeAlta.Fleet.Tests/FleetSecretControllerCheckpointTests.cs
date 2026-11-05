using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// What a controller device keeps of the values it manages (<c>doc/fleet/secrets.md</c>, rotation): the machines it withdrew and the
/// versions it saw, which every request of the desktop reads and writes through a checkpoint of its own over one file.
/// </summary>
[TestClass]
public sealed class FleetSecretControllerCheckpointTests
{
    [TestMethod]
    public async Task TheWithdrawalsOfRequestsAtTheSameTime_AreAllKept()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, FleetSecretControllerCheckpoint.FileName);
        const int Requests = 4;
        const int MachinesEach = 25;

        // Each request of the window makes its own checkpoint over the file of the link, as SecretsOf does, and they run side by side.
        await Task.WhenAll(Enumerable.Range(0, Requests).Select(request => Task.Run(() =>
        {
            var checkpoint = new FleetSecretControllerCheckpoint(path);
            for (var machine = 0; machine < MachinesEach; machine++) checkpoint.Withdraw("secret-1", $"machine-{request}-{machine}", 1);
        })));

        var withdrawn = new FleetSecretControllerCheckpoint(path).Read("secret-1")!.Withdrawn;
        Assert.AreEqual(Requests * MachinesEach, withdrawn.Count, "no request writes over the withdrawal of another");
    }
}
