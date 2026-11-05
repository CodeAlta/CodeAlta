using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>The recovery key: 32 words, the same key in the device list for the same words, and the anchor only it opens.</summary>
[TestClass]
public sealed class FleetRecoveryKeyTests
{
    [TestMethod]
    public void TheWordsGiveBackTheSameKeyHoweverTheyAreTyped()
    {
        using var key = FleetRecoveryKey.Generate();
        var words = key.Words.Split(' ');
        Assert.AreEqual(FleetRecoveryKey.Length, words.Length);
        Assert.IsTrue(FleetRecoveryKey.TryParse(string.Join("\n", words.Select(static word => word.ToUpperInvariant())) + "  ", out var typed));
        using (typed)
        using (var original = key.CreateIdentity())
        using (var again = typed.CreateIdentity())
        {
            Assert.AreEqual(original.DeviceId, again.DeviceId, "the same words, the same key in the device list");
            Assert.AreEqual(string.Empty, original.KeyPath, "held in memory only");
        }

        Assert.IsTrue(FleetRecoveryKey.TryParse(string.Join(", ", words), out var commas));
        commas.Dispose();
        Assert.IsFalse(FleetRecoveryKey.TryParse(string.Join(' ', words.Take(31)), out _), "a word missing");
        Assert.IsFalse(FleetRecoveryKey.TryParse(string.Join(' ', words.Take(31).Append("notaword")), out _), "a word not of the list");
        Assert.IsFalse(FleetRecoveryKey.TryParse(null, out _));
        // A word not of the list is found by its position, never by repeating the words.
        var typo = words.ToArray();
        typo[6] = typo[6] + "x";
        Assert.AreEqual(7, FleetRecoveryKey.UnknownWordPosition(string.Join(' ', typo)));
        Assert.AreEqual(0, FleetRecoveryKey.UnknownWordPosition(string.Join(", ", words)));
        Assert.AreEqual(0, FleetRecoveryKey.UnknownWordPosition(null));
        using var other = FleetRecoveryKey.Generate();
        using (var mine = key.CreateIdentity())
        using (var theirs = other.CreateIdentity())
        {
            Assert.AreNotEqual(mine.DeviceId, theirs.DeviceId);
        }
    }

    [TestMethod]
    public void TheAnchorOpensWithItsRecoveryKeyOnly()
    {
        using var key = FleetRecoveryKey.Generate();
        var sealedText = key.SealAnchor("org-1", "root-hash");
        Assert.IsFalse(sealedText.Contains("root-hash", StringComparison.Ordinal));
        Assert.IsTrue(key.TryOpenAnchor(sealedText, out var organization, out var root));
        Assert.AreEqual(("org-1", "root-hash"), (organization, root));
        using var other = FleetRecoveryKey.Generate();
        Assert.IsFalse(other.TryOpenAnchor(sealedText, out _, out _), "a server cannot make an anchor of its own");
        var json = System.Text.Json.Nodes.JsonNode.Parse(sealedText)!.AsObject();
        var ct = json["ct"]!.GetValue<string>();
        json["ct"] = (ct[0] == 'A' ? "B" : "A") + ct[1..];
        Assert.IsFalse(key.TryOpenAnchor(json.ToJsonString(), out _, out _), "changed");
        Assert.IsFalse(key.TryOpenAnchor(null, out _, out _));
    }
}
