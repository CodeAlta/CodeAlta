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
        var protection = new CodeAlta.Catalog.Secrets.LocalSecretStore(Path.Combine(folder.Path, "global"));
        var sends = new FleetHeldSends(path, protection);
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < FleetHeldSends.MaximumKept; index++) sends.Add(Held($"send-{index}", start.AddSeconds(index)));

        // One more is refused before the node answers that it waits: the oldest, which the node already said waits, stays.
        Assert.ThrowsExactly<FleetHeldSendsFullException>(() => sends.Add(Held("one-too-many", start.AddDays(1))));
        var kept = new FleetHeldSends(path, protection).List();
        Assert.AreEqual(FleetHeldSends.MaximumKept, kept.Count);
        Assert.AreEqual("send-0", kept[0].RunId, "no send already held is evicted");
        Assert.IsFalse(kept.Any(static send => send.RunId == "one-too-many"));

        // A send held again (a retry of the same run) is not one more.
        sends.Add(Held("send-0", start));
    }

    [TestMethod]
    public void AHeldPromptAndItsTitle_AreNeverKeptInClear_AndReadBackAfterARestart()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "fleet", "held-sends.json");
        var protection = new CodeAlta.Catalog.Secrets.LocalSecretStore(Path.Combine(folder.Path, "global"));
        const string prompt = "Deploy with the token ghp_0123456789abcdefABCDEF0123456789abcd.";
        const string title = "Release with ghp_0123456789abcdefABCDEF0123456789abcd";
        new FleetHeldSends(path, protection).Add(Held("send-secret", DateTimeOffset.UtcNow) with { Text = prompt, Title = title });

        // What a prompt holds (a value of a secret a person pasted) is protected as the secrets of the machine are.
        var written = File.ReadAllText(path);
        Assert.IsFalse(written.Contains("ghp_0123456789abcdef", StringComparison.Ordinal), "the file keeps no prompt or title in clear: " + written);
        Assert.IsFalse(written.Contains("Deploy with", StringComparison.Ordinal));

        var kept = new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(Path.Combine(folder.Path, "global"))).List().Single();
        Assert.AreEqual((prompt, title), (kept.Text, kept.Title), "a restart reads the held prompt back");
    }

    private static FleetHeldSend Held(string runId, DateTimeOffset at)
        => new(runId, "session-" + runId, "The held work.", null, "nonce-" + runId, null, null, null, null, at);
}
