using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Desktop.Tests;

/// <summary>What this computer approves for a machine it paired with once the machine joined a server (<see cref="MachinesService.ServerDevice"/>).</summary>
[TestClass]
public sealed class MachineServerJoinTests
{
    [TestMethod]
    public void TheKeyApproved_IsTheOnePairingPinned_NeverTheDeviceOrTheWordsTheMachineAnswers()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-machine-join-").FullName;
        try
        {
            using var machineKey = DeviceIdentity.LoadOrCreate(Path.Combine(folder, "machine"));
            using var attackerKey = DeviceIdentity.LoadOrCreate(Path.Combine(folder, "attacker"));
            var paired = new PairedMachine("build-box", machineKey.DeviceId, machineKey.Fingerprint, ["192.0.2.1:2585"], DateTimeOffset.UnixEpoch, ["manage"]);

            Assert.AreEqual(machineKey.Fingerprint, MachinesService.ServerDevice(paired, new FleetServerJoinResult(machineKey.DeviceId, "build-box", "any words", false)),
                "the whole pinned key, whatever words the machine says");
            // A machine taken over that enrolled another key (a controller of its own) and answers that key's id and words.
            Assert.IsNull(MachinesService.ServerDevice(paired, new FleetServerJoinResult(attackerKey.DeviceId, "build-box", attackerKey.Fingerprint.ToShortWords(), false)));
            Assert.IsNull(MachinesService.ServerDevice(paired with { Fingerprint = default }, new FleetServerJoinResult(machineKey.DeviceId, "build-box", "", false)),
                "a machine without a pinned key is approved by nobody");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
