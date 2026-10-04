using System.Text.Json;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class HistoryCompactionProjectionTests
{
    [TestMethod]
    public void TryProject_KeepsFiguresCountsListsAndReturnsTheSummary()
    {
        var summary = new string('s', 20_000);
        using var document = JsonDocument.Parse($$"""
            {"schema":"codealta.localCompaction.v1","trigger":"threshold","tokensBefore":250000,"tokensAfter":61000,
             "compressionRatio":0.244,"targetMet":true,"isSplitTurn":false,"summaryMarkdown":"{{summary}}",
             "readFiles":["a","b","c"],"modifiedFiles":[],"nested":{"ignored":1},"longText":"{{new string('x', 300)}}","nothing":null}
            """);

        Assert.IsTrue(HistoryCompactionProjection.TryProject(document.RootElement, out var figures, out var projectedSummary));

        Assert.AreEqual(summary, projectedSummary);
        Assert.IsTrue(figures.Length < 512, "The figures stay far below a row's details budget.");
        using var projected = JsonDocument.Parse(figures);
        var root = projected.RootElement;
        Assert.AreEqual("codealta.localCompaction.v1", root.GetProperty("schema").GetString());
        Assert.AreEqual("threshold", root.GetProperty("trigger").GetString());
        Assert.AreEqual(250000, root.GetProperty("tokensBefore").GetInt32());
        Assert.AreEqual(0.244, root.GetProperty("compressionRatio").GetDouble());
        Assert.IsTrue(root.GetProperty("targetMet").GetBoolean());
        Assert.IsFalse(root.GetProperty("isSplitTurn").GetBoolean());
        Assert.AreEqual(3, root.GetProperty("readFilesCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("modifiedFilesCount").GetInt32());
        foreach (var dropped in new[] { "summaryMarkdown", "readFiles", "modifiedFiles", "nested", "longText", "nothing" })
            Assert.IsFalse(root.TryGetProperty(dropped, out _), dropped);
    }

    [TestMethod]
    public void TryProject_RefusesOtherDetailsAndReportsAMissingSummary()
    {
        Assert.IsFalse(HistoryCompactionProjection.TryProject(null, out _, out _));
        using var other = JsonDocument.Parse("""{"schema":"provider.compaction","summaryMarkdown":"x"}""");
        Assert.IsFalse(HistoryCompactionProjection.TryProject(other.RootElement, out _, out _));
        using var list = JsonDocument.Parse("""["codealta.localCompaction.v1"]""");
        Assert.IsFalse(HistoryCompactionProjection.TryProject(list.RootElement, out _, out _));

        using var bare = JsonDocument.Parse("""{"schema":"codealta.localCompaction.v1","summaryMarkdown":"  "}""");
        Assert.IsTrue(HistoryCompactionProjection.TryProject(bare.RootElement, out var figures, out var summary));
        Assert.IsNull(summary);
        Assert.AreEqual("""{"schema":"codealta.localCompaction.v1"}""", figures);
    }
}
