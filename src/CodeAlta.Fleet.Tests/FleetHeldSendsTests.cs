using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The sends a budget holds, kept by the node until their owner continues them (<c>doc/fleet/observability.md</c>, budgets and
/// alerts): a send the node said waits is never lost, neither to a restart nor to the sends held after it.
/// </summary>
[TestClass]
public sealed class FleetHeldSendsTests
{
    [TestMethod]
    public void ASendHeldPastTheBound_IsRefused_AndEvictsNoSendAlreadyHeld()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "held-sends.json");
        var sends = new FleetHeldSends(path);
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < FleetHeldSends.MaximumKept; index++) sends.Add(Held($"send-{index}", start.AddSeconds(index)));

        // One more is refused before the node answers that it waits: the oldest, which the node already said waits, stays.
        Assert.ThrowsExactly<FleetHeldSendsFullException>(() => sends.Add(Held("one-too-many", start.AddDays(1))));
        var kept = new FleetHeldSends(path).List();
        Assert.AreEqual(FleetHeldSends.MaximumKept, kept.Count);
        Assert.AreEqual("send-0", kept[0].RunId, "no send already held is evicted");
        Assert.IsFalse(kept.Any(static send => send.RunId == "one-too-many"));

        // A send held again (a retry of the same run) is not one more.
        sends.Add(Held("send-0", start));
    }

    private static FleetHeldSend Held(string runId, DateTimeOffset at)
        => new(runId, "session-" + runId, "The held work.", null, "nonce-" + runId, null, null, null, null, at);
}
