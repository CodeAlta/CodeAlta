using System.Text;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillIgnoreSourceParserTests
{
    private static SkillIgnoreSourceResult Parse(string text, string? baseDirectory = "", bool complete = true) =>
        SkillIgnoreSourceParser.Parse(Encoding.UTF8.GetBytes(text), baseDirectory, complete);

    [TestMethod]
    public void CompleteEmptyAndIncompleteInputsAreDifferent()
    {
        var empty = Parse("");
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, empty.Status);
        Assert.AreEqual(0, empty.Rules.Count);
        foreach (var bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("!important.md\n") })
        {
            var incomplete = SkillIgnoreSourceParser.Parse(bytes, "", isComplete: false);
            Assert.AreEqual(SkillIgnoreSourceStatus.Incomplete, incomplete.Status);
            Assert.AreEqual(0, incomplete.Rules.Count);
        }
        Assert.Throws<OperationCanceledException>(() => SkillIgnoreSourceParser.Parse([], null, false, new CancellationToken(true)));
    }

    [TestMethod]
    public void CompleteLiteralRulesKeepOrderScopeNegationAndEscapes()
    {
        var result = Parse("# heading\r\n\r\n/hidden/\r\n!hidden/keep.md\r\n\\#literal  \r\n\\!literal\r\nname\\ \r\ncache/\r\n", "nested/folder");
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, result.Status);
        Assert.AreEqual(6, result.Rules.Count);
        Assert.AreEqual("hidden", result.Rules[0].Pattern);
        Assert.AreEqual("/hidden/", result.Rules[0].RawPattern);
        Assert.IsTrue(result.Rules[0].DirectoryOnly);
        Assert.IsFalse(result.Rules[0].BasenameOnly);
        Assert.IsFalse(result.Rules[0].IsNegated);
        Assert.AreEqual(3, result.Rules[0].LineNumber);
        Assert.AreEqual("hidden/keep.md", result.Rules[1].Pattern);
        Assert.IsTrue(result.Rules[1].IsNegated);
        Assert.AreEqual(@"\#literal", result.Rules[2].RawPattern);
        Assert.IsFalse(result.Rules[3].IsNegated);
        Assert.AreEqual(@"name\ ", result.Rules[4].RawPattern);
        Assert.IsTrue(result.Rules[5].BasenameOnly);
        Assert.IsTrue(result.Rules[5].DirectoryOnly);
        Assert.IsTrue(result.Rules.All(rule => rule.BaseDirectory == "nested/folder"));
        Assert.Throws<NotSupportedException>(() => ((IList<SkillIgnoreRule>)result.Rules)[0] = result.Rules[1]);
    }

    [TestMethod]
    public void MalformedAndUnsupportedTextNeverReturnPartialRules()
    {
        var malformed = Parse("good/\n[unterminated\n");
        Assert.AreEqual(SkillIgnoreSourceStatus.InvalidRule, malformed.Status);
        Assert.AreEqual(0, malformed.Rules.Count);
        foreach (var bytes in new byte[][] { [0xC0, 0xAF], [0xEF, 0xBB, 0xBF, 0xC0], [0xFF, 0xFE, 0x00, 0xD8], [0xFF, 0xFE, 0x00] })
        {
            var invalid = SkillIgnoreSourceParser.Parse(bytes, "", true);
            Assert.AreEqual(SkillIgnoreSourceStatus.InvalidEncoding, invalid.Status);
            Assert.AreEqual(0, invalid.Rules.Count);
        }
        Assert.AreEqual(SkillIgnoreSourceStatus.InvalidEncoding, Parse("a\0b").Status);
    }

    [TestMethod]
    public void ByteAndPhysicalLineBudgetsRefuseBeforeParsing()
    {
        var exactBytes = string.Concat(Enumerable.Repeat("#" + new string('x', SkillIgnoreSourceParser.MaximumLineLength - 2) + "\n", 32));
        Assert.AreEqual(SkillIgnoreSourceParser.MaximumBytes, Encoding.UTF8.GetByteCount(exactBytes));
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, Parse(exactBytes).Status);
        var overBytes = SkillIgnoreSourceParser.Parse(Encoding.UTF8.GetBytes(exactBytes).Append((byte)0xFF).ToArray(), "", true);
        Assert.AreEqual(SkillIgnoreSourceStatus.ByteLimit, overBytes.Status);
        Assert.AreEqual(0, overBytes.Rules.Count);
        var exactLines = string.Join('\n', Enumerable.Repeat("#", SkillIgnoreSourceParser.MaximumLines));
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, Parse(exactLines).Status);
        var overLines = Parse(exactLines + "\n!");
        Assert.AreEqual(SkillIgnoreSourceStatus.LineLimit, overLines.Status);
        Assert.AreEqual(0, overLines.Rules.Count);
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, Parse(new string('a', SkillIgnoreSourceParser.MaximumLineLength)).Status);
        var overLineLength = Parse("good\n" + new string('a', SkillIgnoreSourceParser.MaximumLineLength + 1));
        Assert.AreEqual(SkillIgnoreSourceStatus.LineLengthLimit, overLineLength.Status);
        Assert.AreEqual(0, overLineLength.Rules.Count);
    }

    [TestMethod]
    public void CanonicalRelativeBaseAndBomBudgets()
    {
        var exactBase = string.Join('/', Enumerable.Repeat(new string('x', 169), SkillIgnoreSourceParser.MaximumBaseDepth - 1)
            .Append(new string('x', 174)));
        Assert.AreEqual(SkillIgnoreSourceParser.MaximumBaseLength, exactBase.Length);
        Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, Parse("rule", exactBase).Status);
        foreach (var invalidBase in new string?[]
        {
            null, exactBase + "x", new string('x', 256), "a/b/c/d/e/f/g", "/a", "a/", "a//b", "a/./b", "a/../b", @"a\b", "C:/a", "a\0b",
        })
        {
            var result = Parse("rule", invalidBase);
            Assert.AreEqual(SkillIgnoreSourceStatus.InvalidBase, result.Status);
            Assert.AreEqual(0, result.Rules.Count);
        }
        const string text = "über/\n!über/keep.md\n";
        foreach (var (encoding, bom) in new (Encoding, byte[])[]
        {
            (new UTF8Encoding(false, true), []), (new UTF8Encoding(true, true), [0xEF, 0xBB, 0xBF]),
            (new UnicodeEncoding(false, true, true), [0xFF, 0xFE]), (new UnicodeEncoding(true, true, true), [0xFE, 0xFF]),
            (new UTF32Encoding(false, true, true), [0xFF, 0xFE, 0x00, 0x00]),
            (new UTF32Encoding(true, true, true), [0x00, 0x00, 0xFE, 0xFF]),
        })
        {
            var result = SkillIgnoreSourceParser.Parse(bom.Concat(encoding.GetBytes(text)).ToArray(), "", true);
            Assert.AreEqual(SkillIgnoreSourceStatus.Parsed, result.Status, encoding.WebName);
            Assert.AreEqual("über", result.Rules[0].Pattern);
            Assert.IsTrue(result.Rules[1].IsNegated);
        }
    }
}
