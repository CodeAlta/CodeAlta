using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The moves a paired machine gives and a lost connection left unfinished: this computer goes on with those the machine
/// bound to its device, as their target or as the relay to the device the record names, never with a machine it picks by
/// name, even when two paired machines share that name.
/// </summary>
[TestClass]
public sealed class DesktopMoveRouteTests
{
    private const string Own = "device-desktop";
    private const string Source = "device-source";
    private const string Twin1 = "device-twin-1";
    private const string Twin2 = "device-twin-2";

    [TestMethod]
    public void AMoveRelayedToOneOfTwoMachinesOfTheSameName_GoesOnWithTheDeviceItRecorded()
    {
        var paired = new HashSet<string>(StringComparer.Ordinal) { Source, Twin1, Twin2 };

        Assert.IsTrue(DesktopMoves.TryResumeRoute(Record(Own, Twin2), Own, Source, paired.Contains, out var via));
        Assert.AreEqual(Twin2, via, "The record names the device, not the name both machines have.");
    }

    [TestMethod]
    public void AMoveToThisComputer_GoesOnHere_AndOneBoundToAnotherDevice_Nowhere()
    {
        var paired = new HashSet<string>(StringComparer.Ordinal) { Source, Twin1 };

        Assert.IsTrue(DesktopMoves.TryResumeRoute(Record(Own, null), Own, Source, paired.Contains, out var via));
        Assert.IsNull(via);
        Assert.IsFalse(DesktopMoves.TryResumeRoute(Record("device-other-desktop", null), Own, Source, paired.Contains, out _),
            "Another computer drove it: it is its move to go on with.");
        Assert.IsFalse(DesktopMoves.TryResumeRoute(Record(null, null), Own, Source, paired.Contains, out _), "A record that names no device goes on with none.");
        Assert.IsFalse(DesktopMoves.TryResumeRoute(Record(Own, "device-unknown"), Own, Source, paired.Contains, out _));
        Assert.IsFalse(DesktopMoves.TryResumeRoute(Record(Own, Source), Own, Source, paired.Contains, out _), "A machine never gives a session to itself.");
    }

    private static FleetMoveRecord Record(string? toDevice, string? via)
        => new("move-1", SessionMoveRole.Source, SessionMoveState.Offered, "session-1", "source", "twin", 1, null, null, null, DateTimeOffset.UtcNow, toDevice, via);
}
