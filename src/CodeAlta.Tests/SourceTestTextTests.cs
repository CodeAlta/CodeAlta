using System.Text;

namespace CodeAlta.Tests;

/// <summary>Pure string/byte coverage of the source fixtures' shared newline policy.</summary>
[TestClass]
public sealed class SourceTestTextTests
{
    [TestMethod]
    [DataRow("α \nb\nc\n")]
    [DataRow("α \r\nb\r\nc\r\n")]
    [DataRow("α \r\nb\nc\r\n")]
    public void Canonicalize_EquatesLfCrLfAndMixedSourceAndLiterals(string text)
    {
        const string expected = "α \nb\nc\n";
        Assert.AreEqual(expected, SourceTestText.Canonicalize(text));
        Assert.AreEqual(expected, SourceTestText.DecodeSource(Encoding.UTF8.GetBytes(text)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HistoricalBytes_PreservesRecordedRendering(bool crLf)
    {
        var expected = Encoding.UTF8.GetBytes(crLf ? "a\r\nb\r\n" : "a\nb\n");
        foreach (var text in new[] { "a\nb\n", "a\r\nb\r\n", "a\r\nb\n" })
        {
            CollectionAssert.AreEqual(expected, SourceTestText.HistoricalBytes(text, crLf));
        }
    }

    [TestMethod]
    [DataRow("a \nb\n")]
    [DataRow("A\nb\n")]
    [DataRow("a\n\nb\n")]
    [DataRow("a\t\nb\n")]
    public void Canonicalize_PreservesNonNewlineDifferences(string changed)
    {
        Assert.AreNotEqual(SourceTestText.Canonicalize("a\nb\n"), SourceTestText.Canonicalize(changed));
        Assert.AreEqual(changed, SourceTestText.Canonicalize(changed));
    }

    [TestMethod]
    [DataRow("\uFEFFa\n")]
    [DataRow("a\rb\n")]
    [DataRow("a\r\nb\rc\n")]
    public void CanonicalizeAndDecodeSource_RejectBomAndLoneCr(string invalid)
    {
        Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.Canonicalize(invalid));
        var bytes = Encoding.UTF8.GetBytes(invalid);
        Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(bytes));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void DecodeSource_RejectsMalformedUtf8(int scenario)
    {
        byte[] bytes = scenario switch
        {
            0 => [0xC0, 0x80, 0x0A],
            1 => [0xFF, 0x0A],
            2 => [0xE2, 0x82],
            _ => throw new AssertFailedException("Unknown row."),
        };
        Assert.ThrowsExactly<DecoderFallbackException>(() => SourceTestText.DecodeSource(bytes));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("a")]
    [DataRow("a\r\nb")]
    public void DecodeSource_RequiresFinalNewline(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(bytes));
        Assert.AreEqual(text.Replace("\r\n", "\n", StringComparison.Ordinal), SourceTestText.Canonicalize(text));
    }
}
