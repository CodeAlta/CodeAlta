using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>The reader gives the same records wherever the chunks of the file end, and whatever the bound of a parsed record is.</summary>
[TestClass]
public sealed class JournalScannerRobustnessTests
{
    private static string Describe(IReadOnlyList<JournalRecord> records)
        => string.Join('\n', records.Select(static record => $"{record.Kind}|{record.Timestamp:O}|{record.RunId}|{record.Provider}|{record.Offset}|{record.Length}|{record.Oversize}"));

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(7)]
    [DataRow(31)]
    [DataRow(159)]
    [DataRow(160)]
    [DataRow(161)]
    [DataRow(512)]
    [DataRow(4096)]
    [DataRow(1 << 20)]
    public void Scan_GivesTheSameRecordsForAnySizeOfReadBuffer(int readBufferBytes)
    {
        var journal = SplitInvarianceTests.RichJournal();
        using var reference = new JournalScanner();
        var expected = new CollectingSink();
        using (var stream = journal.ToStream())
        {
            reference.Scan(stream, 0, null, expected, long.MaxValue, CancellationToken.None);
        }

        using var scanner = new JournalScanner(new JournalScanOptions { ReadBufferBytes = readBufferBytes });
        var actual = new CollectingSink();
        using var small = journal.ToStream();
        var result = scanner.Scan(small, 0, null, actual, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(Describe(expected.Records), Describe(actual.Records));
        Assert.AreEqual(journal.ToBytes().Length, result.EndOffset);
    }

    [TestMethod]
    [DataRow(640)]
    [DataRow(700)]
    [DataRow(1024)]
    [DataRow(2000)]
    [DataRow(1 << 20)]
    public void Scan_GivesTheSameFactsForAnyBoundOfAParsedRecord_WhenNothingIsLargerThanTheBound(int bound)
    {
        var journal = SplitInvarianceTests.RichJournal();
        using var reference = new SessionCatchUp();
        using var referenceStream = journal.ToStream();
        var expected = reference.CatchUp(journal.SessionId, referenceStream, null, long.MaxValue, CancellationToken.None);

        using var catchUp = new SessionCatchUp(new JournalScanOptions { MaxParsedRecordBytes = bound, ReadBufferBytes = 97 });
        using var stream = journal.ToStream();
        var actual = catchUp.CatchUp(journal.SessionId, stream, null, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(0, actual.Scan.MalformedLines);
        Assert.AreEqual(expected.Batch.Runs.Count, actual.Batch.Runs.Count);
        foreach (var (runId, row) in expected.Batch.Runs)
        {
            Assert.AreEqual(row.Outcome, actual.Batch.Runs[runId].Outcome, "a run ends where it ended whatever the bound");
            Assert.AreEqual(row.Duration, actual.Batch.Runs[runId].Duration);
        }

        if (actual.Scan.OversizeRecords == 0)
        {
            Assert.AreEqual(expected.Batch.ToCanonicalText(), actual.Batch.ToCanonicalText());
        }
    }

    [TestMethod]
    public void Scan_AnOversizeRecordKeepsItsTimeItsRunAndItsPlaceInTheRun()
    {
        var huge = new string('z', 200_000);
        var b = new JournalBuilder();
        var t0 = JournalBuilder.Time(0);
        b.ModelChanged(t0, "r1", "codex", "m", "Low")
            .User(t0.AddSeconds(1), "r1", "go")
            .ToolStarted(t0.AddSeconds(2), "r1", "t1", "read_file", "{\"path\":\"p\"}")
            .ToolDone(t0.AddSeconds(5), "r1", "t1", "read_file", resultText: huge, readFiles: ["p"])
            .User(t0.AddSeconds(6), "r1", huge)
            .Idle(t0.AddSeconds(7), "r1");

        using var catchUp = new SessionCatchUp(new JournalScanOptions { MaxParsedRecordBytes = 4096, ReadBufferBytes = 1000 });
        using var stream = b.ToStream();
        var result = catchUp.CatchUp(b.SessionId, stream, null, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(2, result.Scan.OversizeRecords);
        Assert.AreEqual(0, result.Scan.MalformedLines);
        var tool = result.Batch.Tools.Single().Value;
        Assert.AreEqual(1, tool.Calls);
        Assert.AreEqual(3000, tool.DurationMsTotal, "the end of the call is placed by the end of its line");
        Assert.AreEqual(1, tool.FilesRead);
        Assert.IsTrue(tool.BytesOut > huge.Length);
        var prompt = result.Batch.Content.Where(static pair => pair.Key.Kind == ContentKind.Prompt).Sum(static pair => pair.Value.Chars);
        Assert.IsTrue(prompt >= huge.Length, "the size of a prompt that is too large to parse is that of its line");
        Assert.AreEqual(RunOutcome.Completed, result.Batch.Runs["r1"].Outcome);
        Assert.AreEqual(7_000, result.Batch.Runs["r1"].Duration.TotalMilliseconds);
    }

    [TestMethod]
    public void Scan_ALineOfTheSizeOfTheBoundIsParsedWhole()
    {
        var b = new JournalBuilder();
        var t0 = JournalBuilder.Time(0);
        b.User(t0, "r1", new string('a', 2000));
        var lineLength = b.Lines[0].Length;

        using var exact = new JournalScanner(new JournalScanOptions { MaxParsedRecordBytes = lineLength });
        var sink = new CollectingSink();
        using var stream = b.ToStream();
        var result = exact.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(0, result.OversizeRecords);
        Assert.AreEqual(2000, ((UserContentRecord)sink.Records[0]).Chars);

        using var shorter = new JournalScanner(new JournalScanOptions { MaxParsedRecordBytes = lineLength - 1 });
        var other = new CollectingSink();
        using var stream2 = b.ToStream();
        var result2 = shorter.Scan(stream2, 0, null, other, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(1, result2.OversizeRecords);
    }

    [TestMethod]
    public void Scan_ReadsAJournalOfABillionBytesLikeLineWithoutHoldingIt()
    {
        // A line of 64 MiB written to a file and passed over: the memory of the process must not grow with it.
        var path = Path.Combine(Path.GetTempPath(), "codealta-big-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var t0 = JournalBuilder.Time(0);
            var b = new JournalBuilder();
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var prefix = System.Text.Encoding.UTF8.GetBytes($"{{\"$type\":\"raw\",\"backendEventType\":\"local.sessionState\",\"raw\":{{\"padding\":\"");
                var suffix = System.Text.Encoding.UTF8.GetBytes($"\"}},{b.Envelope(t0, null)}\n");
                file.Write(prefix);
                var chunk = new byte[1 << 20];
                Array.Fill(chunk, (byte)'p');
                for (var index = 0; index < 64; index++)
                {
                    file.Write(chunk);
                }

                file.Write(suffix);
                file.Write(System.Text.Encoding.UTF8.GetBytes(b.User(t0.AddSeconds(1), "r1", "after").Lines[0] + "\n"));
            }

            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            using var scanner = new JournalScanner();
            var sink = new CollectingSink();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var result = scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.AreEqual(2, sink.Records.Count);
            Assert.AreEqual(0, result.OversizeRecords, "a record that is passed over is not an oversize record");
            Assert.IsTrue(result.SkippedBytes > 64L * 1024 * 1024);
            Assert.IsTrue(allocated < 8 * 1024 * 1024, $"allocated {allocated} bytes to pass over 64 MiB");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
