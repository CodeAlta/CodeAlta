using CodeAlta.Agent;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class FleetPublicationOutboxTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AChangeIsANewVersion_AndWhatTheServerHoldsIsNotSentAgain()
    {
        using var folder = new TestFolder();
        var outbox = FleetPublicationOutbox.Load(Path.Combine(folder.Path, "outbox.json"));
        outbox.Resume(new FleetPublicationCursors(0, 0));

        var first = outbox.Pending([Session("s1"), Session("s2")], 500);
        Assert.AreEqual(2, first.Count);
        Assert.IsTrue(first.All(static fact => fact.Version == 1));
        var batch = outbox.Batch(first);
        outbox.Acknowledged(new FleetFactsPublishResult(batch.Sequence, 2, 0, []));
        Assert.AreEqual(0, outbox.Pending([Session("s1"), Session("s2")], 500).Count, "unchanged, nothing to send");

        var changed = outbox.Pending([Session("s1", FleetSessionStates.Running), Session("s2")], 500);
        Assert.AreEqual(("s1", 2L), (changed.Single().SessionId, changed.Single().Version));
    }

    [TestMethod]
    public void ABatchWhoseAnswerWasLostIsAcknowledgedByTheCursorsOfTheWelcome()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "outbox.json");
        var outbox = FleetPublicationOutbox.Load(path);
        outbox.Resume(new FleetPublicationCursors(0, 0));
        var batch = outbox.Batch(outbox.Pending([Session("s1")], 500));
        Assert.IsTrue(outbox.HasInFlight);

        // The connection dropped before the answer; the server had applied the batch, and says so in its welcome.
        var reloaded = FleetPublicationOutbox.Load(path);
        reloaded.Resume(new FleetPublicationCursors(batch.Sequence, 0));
        Assert.IsFalse(reloaded.HasInFlight);
        Assert.AreEqual(0, reloaded.Pending([Session("s1")], 500).Count);

        // Had the server not applied it, the facts are sent again in a batch after its cursor.
        var other = FleetPublicationOutbox.Load(Path.Combine(folder.Path, "other.json"));
        other.Resume(new FleetPublicationCursors(0, 0));
        var lost = other.Batch(other.Pending([Session("s1")], 500));
        other.Resume(new FleetPublicationCursors(lost.Sequence - 1, 0));
        var again = other.Pending([Session("s1")], 500);
        Assert.AreEqual(1L, again.Single().Version);
        Assert.IsTrue(other.Batch(again).Sequence > lost.Sequence, "a sequence is never used twice");
    }

    [TestMethod]
    public void AServerBehindWhatItAcknowledged_GetsEverythingAgain()
    {
        using var folder = new TestFolder();
        var outbox = FleetPublicationOutbox.Load(Path.Combine(folder.Path, "outbox.json"));
        outbox.Resume(new FleetPublicationCursors(0, 0));
        var batch = outbox.Batch(outbox.Pending([Session("s1")], 500));
        outbox.Acknowledged(new FleetFactsPublishResult(batch.Sequence, 1, 0, []));
        var inventory = new FleetMachineInventory("Mac mini", "macos");
        var publication = outbox.Inventory(inventory)!.Value;
        outbox.InventoryAcknowledged(publication.Publication.Sequence, publication.Publication.Sequence, publication.Hash);
        Assert.IsNull(outbox.Inventory(inventory));

        // A server restored from a backup is behind: the facts and the inventory are published again.
        outbox.Resume(new FleetPublicationCursors(0, 0));
        Assert.AreEqual(1, outbox.Pending([Session("s1")], 500).Count);
        Assert.IsNotNull(outbox.Inventory(inventory));
    }

    [TestMethod]
    public void ASessionCreatedByAPrivateOneIsPrivate()
    {
        var created = new AgentSessionOptions().Within(new AgentSessionOptions { Private = true });
        Assert.IsTrue(created.Private);
        Assert.IsFalse(new AgentSessionOptions().Within(AgentSessionOptions.Default).Private);
        Assert.IsTrue(AgentSessionOptions.Default.IsDefault);
        Assert.IsFalse(new AgentSessionOptions { Private = true }.IsDefault, "a Private session records its options");
    }

    private static FleetLocalSession Session(string id, string state = FleetSessionStates.Idle)
        => new(id, "work", false, 0, state, Created, Created.AddMinutes(1), "anthropic", "claude-opus-5-5");
}
