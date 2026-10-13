using CodeAlta.Agent.Diffing;

namespace CodeAlta.Tests;

/// <summary>The lines a change adds and removes, as its unified diff counts them.</summary>
[TestClass]
public sealed class UnifiedDiffChangedLinesTests
{
    [TestMethod]
    public void CountChangedLines_CountsTheAddedAndRemovedLinesOfADiff()
    {
        Assert.AreEqual((0, 0), UnifiedDiffBuilder.CountChangedLines(string.Empty, string.Empty));
        Assert.AreEqual((0, 0), UnifiedDiffBuilder.CountChangedLines("one\ntwo\n", "one\ntwo\n"));
        Assert.AreEqual((2, 1), UnifiedDiffBuilder.CountChangedLines("one\ntwo\nthree\n", "one\ntwo and a half\nthree\nfour\n"));
        Assert.AreEqual((0, 2), UnifiedDiffBuilder.CountChangedLines("one\ntwo\nthree\n", "two\n"));
        Assert.AreEqual((3, 0), UnifiedDiffBuilder.CountChangedLines(string.Empty, "one\ntwo\nthree"));
        // The same lines with other line ends are the same lines.
        Assert.AreEqual((0, 0), UnifiedDiffBuilder.CountChangedLines("one\r\ntwo\r\n", "one\ntwo\n"));
        Assert.AreEqual((1, 1), UnifiedDiffBuilder.CountChangedLines("one\r\ntwo\r\n", "one\nTWO\n"));
        // The counts are those of the unified diff of the same texts.
        var diff = UnifiedDiffBuilder.CreateUnifiedDiff("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n", "old", "new");
        var lines = diff.Split('\n').Skip(2).ToArray();
        Assert.AreEqual((lines.Count(static line => line.StartsWith('+')), lines.Count(static line => line.StartsWith('-'))),
            UnifiedDiffBuilder.CountChangedLines("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n"));
        Assert.ThrowsExactly<ArgumentNullException>(() => UnifiedDiffBuilder.CountChangedLines(null!, string.Empty));
        Assert.ThrowsExactly<ArgumentNullException>(() => UnifiedDiffBuilder.CountChangedLines(string.Empty, null!));
    }
}
