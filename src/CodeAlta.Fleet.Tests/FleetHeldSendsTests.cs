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

    [TestMethod]
    public void TheSendsAnEarlierVersionKeptInClear_AreTakenAgain_AndNoLongerKeptInClear()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "fleet", "held-sends.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The file an earlier version wrote: a list of the sends, their prompts in clear.
        File.WriteAllText(path, """
            [{"RunId":"send-old","SessionId":"session-old","Text":"Deploy with ghp_0123456789abcdefABCDEF0123456789abcd.","Title":"Release","Nonce":"nonce-old",
              "ProviderKey":null,"AgentPromptId":null,"ModelId":null,"ReasoningEffort":null,"HeldAt":"2026-11-04T10:00:00+00:00"}]
            """);
        var protection = new CodeAlta.Catalog.Secrets.LocalSecretStore(Path.Combine(folder.Path, "global"));

        // The send the node said waits is still held, and its prompt is no longer on disk in clear.
        var kept = new FleetHeldSends(path, protection).List().Single();
        Assert.AreEqual(("send-old", "Deploy with ghp_0123456789abcdefABCDEF0123456789abcd."), (kept.RunId, kept.Text));
        Assert.IsFalse(File.ReadAllText(path).Contains("ghp_0123456789abcdef", StringComparison.Ordinal), "the earlier file is replaced by protected entries");
        Assert.AreEqual("send-old", new FleetHeldSends(path, protection).List().Single().RunId, "and read back after a restart");
    }

    [TestMethod]
    public void AFileOfHeldSendsThatDoesNotRead_IsKeptAside_NotOverwritten()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "held-sends.json");
        File.WriteAllText(path, "{ damaged");
        var sends = new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(Path.Combine(folder.Path, "global")));
        sends.Add(Held("send-new", DateTimeOffset.UtcNow));
        Assert.IsTrue(Directory.EnumerateFiles(folder.Path, "held-sends.json.unreadable-*").Any(file => File.ReadAllText(file) == "{ damaged"),
            "what the node cannot read is kept for its owner, never overwritten by the next send held");
    }

    [TestMethod]
    public void ASendThisMachineCannotOpenNow_StaysHeldAndWaiting_AndIsTakenOnceItOpens()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "held-sends.json");
        var protector = new SwitchingProtector();
        var global = Path.Combine(folder.Path, "global");
        new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(global, protector)).Add(Held("send-blocked", DateTimeOffset.UtcNow));

        // At the next start the protected store of the machine is away: the send is not opened, nor lost, nor shorter the queue.
        protector.Available = false;
        var sends = new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(global, protector));
        Assert.AreEqual(0, sends.List().Count, "what cannot be opened is not sent");
        CollectionAssert.Contains(sends.SessionIds.ToList(), "session-send-blocked", "its session still waits for a person");
        Assert.AreEqual(1, sends.Blocked, "it is reported as held and not opened");
        Assert.IsTrue(File.ReadAllText(path).Contains("send-blocked", StringComparison.Ordinal), "it stays in the file, as it was written");

        // The store is back: the same reader opens it at its next reading, and a send held after it keeps it.
        protector.Available = true;
        Assert.AreEqual("send-blocked", sends.List().Single().RunId);
        sends.Add(Held("send-other", DateTimeOffset.UtcNow.AddSeconds(1)));
        CollectionAssert.AreEquivalent(new[] { "send-blocked", "send-other" }, new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(global, protector))
            .List().Select(static send => send.RunId).ToArray());
        Assert.AreEqual(0, sends.Blocked);
    }

    [TestMethod]
    public void CountingTheSendsThatDoNotOpen_OpensNone_SoThatTheReaderThatListsThemTakesThem()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "held-sends.json");
        var protector = new SwitchingProtector();
        var global = Path.Combine(folder.Path, "global");
        new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(global, protector)).Add(Held("send-blocked", DateTimeOffset.UtcNow));
        protector.Available = false;
        var sends = new FleetHeldSends(path, new CodeAlta.Catalog.Secrets.LocalSecretStore(global, protector));
        Assert.AreEqual(0, sends.List().Count);

        // The store comes back between the listing and the count: the count says it is still to be taken, the next listing gives it.
        protector.Available = true;
        Assert.AreEqual(1, sends.Blocked, "counting opens nothing: what opens is given by the next listing, to the reader that takes it");
        Assert.AreEqual("send-blocked", sends.List().Single().RunId);
        Assert.AreEqual(0, sends.Blocked);
    }

    // A protection whose store goes away and comes back.
    private sealed class SwitchingProtector : CodeAlta.Catalog.Secrets.ISecretProtector
    {
        public bool Available { get; set; } = true;

        public string Kind => CodeAlta.Catalog.Secrets.SecretProtectionKinds.File;

        public byte[] Protect(ReadOnlySpan<byte> value) => Available ? [.. value.ToArray().Reverse()] : throw new CodeAlta.Catalog.Secrets.SecretStoreUnavailableException("away");

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => Available ? [.. protectedValue.ToArray().Reverse()] : throw new CodeAlta.Catalog.Secrets.SecretStoreUnavailableException("away");
    }

    private static FleetHeldSend Held(string runId, DateTimeOffset at)
        => new(runId, "session-" + runId, "The held work.", null, "nonce-" + runId, null, null, null, null, at);
}
