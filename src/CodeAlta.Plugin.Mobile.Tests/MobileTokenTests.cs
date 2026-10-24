namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileTokenTests
{
    [TestMethod]
    public void AToken_IsRandomUrlSafeAndComparedWhole()
    {
        var token = MobileToken.Create();
        Assert.IsTrue(MobileToken.IsValid(token));
        Assert.AreNotEqual(token, MobileToken.Create());
        Assert.IsTrue(token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "It goes in an address as it is.");
        Assert.IsTrue(MobileToken.Matches(token, token));
        Assert.IsFalse(MobileToken.Matches(token[..^1], token));
        Assert.IsFalse(MobileToken.Matches(null, token));
        Assert.IsFalse(MobileToken.IsValid("short"));
        Assert.IsFalse(MobileToken.IsValid(new string('a', 39) + "/"));
    }

    [TestMethod]
    public void TheQrCode_IsSquare_WithItsFinderPatterns()
    {
        var rows = MobileQr.Rows("https://desk.tail1234.ts.net:2584/#k=" + MobileToken.Create());

        Assert.IsTrue(rows.Count >= 21);
        Assert.IsTrue(rows.All(row => row.Length == rows.Count && row.All(static module => module is '0' or '1')));
        // The finder pattern of the top left corner, without a quiet zone.
        Assert.AreEqual("1111111", rows[0][..7]);
        Assert.AreEqual("1000001", rows[1][..7]);
        Assert.AreEqual("1111111", rows[0][^7..]);
        Assert.AreEqual("1111111", rows[^1][..7]);
    }
}
