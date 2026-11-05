using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class DeviceFingerprintTests
{
    [TestMethod]
    public void FingerprintIsTheHashOfThePublicKeyAndStable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();

        var fingerprint = DeviceFingerprint.FromSubjectPublicKeyInfo(publicKey);

        CollectionAssert.AreEqual(SHA256.HashData(publicKey), fingerprint.AsSpan().ToArray());
        Assert.AreEqual(fingerprint, DeviceFingerprint.FromSubjectPublicKeyInfo(publicKey));
        Assert.AreEqual(fingerprint.GetHashCode(), DeviceFingerprint.FromSubjectPublicKeyInfo(publicKey).GetHashCode());
        Assert.AreEqual(fingerprint.ToShortWords(), DeviceFingerprint.FromSubjectPublicKeyInfo(publicKey).ToShortWords());
    }

    [TestMethod]
    public void CanonicalFormRoundTrips()
    {
        var fingerprint = DeviceFingerprint.FromBytes(Enumerable.Range(0, 32).Select(static value => (byte)(value * 7)).ToArray());

        var text = fingerprint.ToString();

        Assert.AreEqual(43, text.Length);
        Assert.AreEqual(fingerprint, DeviceFingerprint.Parse(text));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("too-short")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public void MalformedFingerprintsAreRefused(string? text)
    {
        Assert.IsFalse(DeviceFingerprint.TryParse(text, out var fingerprint));
        Assert.IsTrue(fingerprint.IsEmpty);
    }

    [TestMethod]
    public void DisplayFormIsGroupsOfBase32()
    {
        var fingerprint = DeviceFingerprint.FromBytes(new byte[32]);

        Assert.AreEqual("AAAA AAAA AAAA AAAA AAAA AAAA AAAA AAAA", fingerprint.ToDisplayString());

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var display = DeviceFingerprint.FromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo()).ToDisplayString();
        Assert.IsTrue(Regex.IsMatch(display, "^([A-Z2-7]{4} ){7}[A-Z2-7]{4}$"), display);
    }

    [TestMethod]
    public void Base32FollowsRfc4648()
    {
        Assert.AreEqual("", Base32.Encode(""u8));
        Assert.AreEqual("MY", Base32.Encode("f"u8));
        Assert.AreEqual("MZXW6", Base32.Encode("foo"u8));
        Assert.AreEqual("MZXW6YTBOI", Base32.Encode("foobar"u8));
    }

    [TestMethod]
    public void WordsAreDistinctAndOnePerByte()
    {
        Assert.HasCount(256, PairingWords.Words);
        Assert.HasCount(256, PairingWords.Words.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.IsTrue(PairingWords.Words.All(static word => word.Length is >= 3 and <= 7 && word.All(char.IsAsciiLetterLower)));

        var fingerprint = DeviceFingerprint.FromBytes(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
        Assert.AreEqual("acid acorn actor agent alarm album", fingerprint.ToShortWords());
    }

    [TestMethod]
    public void ComparisonWordsAreTheSameOnBothSides()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var third = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var a = DeviceFingerprint.FromSubjectPublicKeyInfo(first.ExportSubjectPublicKeyInfo());
        var b = DeviceFingerprint.FromSubjectPublicKeyInfo(second.ExportSubjectPublicKeyInfo());
        var c = DeviceFingerprint.FromSubjectPublicKeyInfo(third.ExportSubjectPublicKeyInfo());

        var words = DeviceFingerprint.ComparisonWords(a, b);

        Assert.AreEqual(words, DeviceFingerprint.ComparisonWords(b, a));
        Assert.HasCount(6, words.Split(' '));
        Assert.AreNotEqual(words, DeviceFingerprint.ComparisonWords(a, c));
        Assert.ThrowsExactly<ArgumentException>(() => DeviceFingerprint.ComparisonWords(a, default));
    }
}
