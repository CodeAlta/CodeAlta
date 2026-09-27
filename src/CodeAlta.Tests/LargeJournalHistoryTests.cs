using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>Disposable journals only; no catalog, provider or user-host initialization.</summary>
[TestClass]
public sealed class LargeJournalHistoryTests
{
    [TestMethod]
    public async Task ExistingReader_ReproducesLargeNewestAndOlderBoundary()
    {
        var large = Line(new string('x', 2 * 1024 * 1024));
        await WithJournal(large, async (stream, stamp) =>
        {
            var error = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadTailAsync(stream, "selected", null, stamp, CancellationToken.None));
            Assert.AreEqual("record_too_large", error.Code);
        });
        await WithJournal(large + Line("healthy newest"), async (stream, stamp) =>
        {
            var newest = await AgentJournalHistoryReader.ReadTailAsync(stream, "selected", null, stamp, CancellationToken.None);
            Assert.AreEqual("healthy newest", ((AgentContentCompletedEvent)newest.Entries.Single().Event).Content);
            Assert.IsNotNull(newest.Next);
            var error = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadTailAsync(stream, "selected", newest.Next, stamp, CancellationToken.None));
            Assert.AreEqual("record_too_large", error.Code);
        });
    }

    [TestMethod]
    public async Task Timeline_LoadsMultiMiBNewestAndOlder_AndAllSourceChunksWithoutMutation()
    {
        var large = Line(new string('x', 2 * 1024 * 1024)).Replace("xxxxx", "日🙂", StringComparison.Ordinal);
        var text = Line("oldest") + large + Line("newest");
        await WithJournal(text, async (stream, stamp) =>
        {
            var initialStamp = stamp();
            AgentSessionHistoryCursor? cursor = null;
            var found = new List<AgentSessionHistoryEntry>();
            AgentHistoryRevision? revision = null;
            do
            {
                var page = await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", cursor, stamp, CancellationToken.None);
                Assert.IsNotNull(page.Revision);
                revision = page.Revision;
                found.InsertRange(0, page.Entries);
                Assert.IsTrue(page.Next is null || page.Next.Offset < (cursor?.Offset ?? stream.Length));
                cursor = page.Next;
            } while (cursor is not null);
            Assert.AreEqual(3, found.Count);
            Assert.AreEqual("oldest", ((AgentContentCompletedEvent)found[0].Event).Content);
            Assert.AreEqual("newest", ((AgentContentCompletedEvent)found[2].Event).Content);
            var entry = found[1];
            var collected = new StringBuilder();
            long? offset = entry.Offset;
            var chunks = 0;
            while (offset is { } current)
            {
                var chunk = await AgentJournalHistoryReader.ReadSourceAsync(stream, revision!, entry.Offset, entry.SourceEnd!.Value, current, stamp, CancellationToken.None);
                Assert.IsTrue(Encoding.UTF8.GetByteCount(chunk.Text) <= AgentJournalHistoryReader.SourceChunkBytes);
                Assert.IsTrue(chunk.NextOffset is null || chunk.NextOffset > current);
                collected.Append(chunk.Text); offset = chunk.NextOffset; chunks++;
            }
            Assert.IsTrue(chunks > 100);
            Assert.AreEqual(large, collected.ToString());
            Assert.AreEqual(initialStamp, stamp());
        });
        await WithJournal(large, async (stream, stamp) =>
        {
            var page = await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None);
            Assert.AreEqual(1, page.Entries.Count); Assert.IsNull(page.Next);
        });
    }

    [TestMethod]
    public async Task Timeline_RecordCeilingAndCorruptionRemainExplicit()
    {
        var overhead = Encoding.UTF8.GetByteCount(Line(""));
        var exact = Line(new string('a', AgentJournalHistoryReader.TimelineRecordBytes - overhead));
        await WithJournal(exact, async (stream, stamp) => Assert.AreEqual(1,
            (await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None)).Entries.Count));
        await WithJournal(exact.Replace("aaa", "aaaa", StringComparison.Ordinal), async (stream, stamp) =>
            Assert.AreEqual("record_too_large", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None))).Code));
        var broken = "{\"content\":\"" + new string('x', 300_000);
        await WithJournal(broken, async (stream, stamp) => Assert.IsTrue(
            (await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None)).TailOmitted));
        await WithJournal(broken + "\n" + Line("newest"), async (stream, stamp) =>
        {
            var page = await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None);
            Assert.AreEqual("corrupt_record", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", page.Next, stamp, CancellationToken.None))).Code);
        });
    }

    [TestMethod]
    public async Task Timeline_RevisionCursorCancellationAndUtf8AreFenced()
    {
        await WithJournal("\ufeff" + Line(new string('a', 300_000)).Replace("\n", "\r\n") + Line("latest"), async (stream, stamp) =>
        {
            var page = await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None);
            Assert.IsNotNull(page.Next);
            foreach (var (cursor, code) in new[] { (page.Next with { SessionId = "other" }, "invalid_cursor"),
                (page.Next with { Offset = page.Next.Offset - 1 }, "invalid_cursor"),
                (page.Next with { LastWriteUtcTicks = page.Next.LastWriteUtcTicks - 1 }, "history_changed") })
                Assert.AreEqual(code, (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                    AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", cursor, stamp, CancellationToken.None))).Code);
            var older = await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", page.Next, stamp, CancellationToken.None);
            Assert.AreEqual(3L, older.Entries.Single().Offset);
            var count = 0;
            Assert.AreEqual("history_changed", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null,
                    () => stamp() with { LastWriteUtcTicks = stamp().LastWriteUtcTicks + count++ }, CancellationToken.None))).Code);
            var source = older.Entries.Single();
            Assert.AreEqual("history_changed", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadSourceAsync(stream, older.Revision!, source.Offset, source.SourceEnd!.Value, source.Offset,
                    () => stamp() with { Length = stamp().Length + 1 }, CancellationToken.None))).Code);
            await Assert.ThrowsAsync<OperationCanceledException>(() => AgentJournalHistoryReader.ReadTimelineAsync(stream,
                "selected", null, stamp, new CancellationToken(true)));
            await File.AppendAllTextAsync(stream.Name, Line("concurrent append"), new UTF8Encoding(false));
            Assert.AreEqual("history_changed", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", page.Next, stamp, CancellationToken.None))).Code);
            Assert.AreEqual("history_changed", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                AgentJournalHistoryReader.ReadSourceAsync(stream, older.Revision!, source.Offset, source.SourceEnd!.Value, source.Offset,
                    stamp, CancellationToken.None))).Code);
        });
        using var invalid = new MemoryStream([0xff]);
        Assert.AreEqual("unsupported_format", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
            AgentJournalHistoryReader.ReadTimelineAsync(invalid, "selected", null, () => new(1, 7), CancellationToken.None))).Code);
    }

    [TestMethod]
    public async Task Timeline_DeserializationFailuresClassifyTailAndInteriorAtBothSizes()
    {
        foreach (var size in new[] { 20, 300_000 })
        {
            var valid = Line(new string('x', size));
            foreach (var record in new[] {
                valid[..^10], // Truncated canonical event, with valid discriminator and required fields.
                valid.Replace("\"contentCompleted\"", "\"unsupported-fixture\"", StringComparison.Ordinal),
                "{\"content\":\"" + new string('x', size),
                "{\"content\":\"" + new string('x', size) + "\"}",
                "{\"$type\":\"contentCompleted\",\"content\":" + new string('x', size) + "}" })
            {
                await WithJournal(record, async (stream, stamp) =>
                {
                    Assert.IsTrue((await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", null, stamp, CancellationToken.None)).TailOmitted);
                    if (size < 128 * 1024)
                        Assert.IsTrue((await AgentJournalHistoryReader.ReadAsync(stream, "selected", null, stamp, CancellationToken.None)).TailOmitted);
                });
                await WithJournal(record.TrimEnd('\n') + "\n" + Line("healthy"), async (stream, stamp) =>
                {
                    AgentSessionHistoryCursor? cursor = null;
                    var error = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(async () =>
                    {
                        do { cursor = (await AgentJournalHistoryReader.ReadTimelineAsync(stream, "selected", cursor, stamp, CancellationToken.None)).Next; }
                        while (cursor is not null);
                    });
                    Assert.AreEqual("corrupt_record", error.Code);
                    if (size < 128 * 1024)
                        Assert.AreEqual("corrupt_record", (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() =>
                            AgentJournalHistoryReader.ReadAsync(stream, "selected", null, stamp, CancellationToken.None))).Code);
                });
            }
        }
    }

    private static string Line(string content) => JsonSerializer.Serialize<AgentEvent>(
        new AgentContentCompletedEvent(new("fixture"), "selected", DateTimeOffset.UnixEpoch, null,
            AgentContentKind.Assistant, "content", null, content), AgentJsonSerializerContext.Default.AgentEvent) + "\n";

    private static async Task WithJournal(string text, Func<FileStream, Func<AgentJournalHistoryReader.Stamp>, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"codealta-large-history-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false, true));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, true);
            await body(stream, () => { var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc.Ticks); });
        }
        finally { File.Delete(path); }
    }
}
