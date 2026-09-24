using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>Inert streams and literal callbacks only; no store, cache or filesystem acquisition.</summary>
[TestClass]
public sealed class AgentSessionHistoryTests
{
    [TestMethod]
    public async Task ReadPage_BoundsBytesRecordsAndAllocation()
    {
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(" \n", 100_000))));
        var page = await Read(stream);
        Assert.AreEqual(0, page.Entries.Count);
        Assert.AreEqual(200L, page.Next!.Offset);
        Assert.IsTrue(stream.BytesRead <= AgentJournalHistoryReader.PageBytes + 5);
        Assert.IsTrue(stream.MaximumRequest <= AgentJournalHistoryReader.PageBytes);
        using var huge = new ProbeStream(Encoding.UTF8.GetBytes(new string('x', 400_000)));
        await Error("record_too_large", () => Read(huge));
        Assert.IsTrue(huge.BytesRead <= AgentJournalHistoryReader.PageBytes + 5);

        // The byte window ends inside record four, not at the 100-record quota.
        var lines = Enumerable.Range(0, 5).Select(i => Line(new AgentContentCompletedEvent(new("p"),
            "runtime-other", DateTimeOffset.UnixEpoch, null, AgentContentKind.Assistant,
            i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, new string('a', 70 * 1024)))).ToArray();
        using var window = new ProbeStream(Encoding.UTF8.GetBytes(string.Concat(lines)));
        var first = await Read(window);
        Assert.AreEqual(3, first.Entries.Count);
        var boundary = Encoding.UTF8.GetByteCount(string.Concat(lines.Take(3)));
        Assert.AreEqual((long)boundary, first.Next!.Offset);
        Assert.IsTrue(window.BytesRead <= AgentJournalHistoryReader.PageBytes + 5);
        var readsBeforeSecond = window.BytesRead;
        var second = await Read(window, first.Next);
        Assert.IsNull(second.Next);
        Assert.AreEqual(2, second.Entries.Count);
        Assert.AreEqual((long)boundary, second.Entries[0].Offset);
        Assert.IsTrue(window.BytesRead - readsBeforeSecond <= AgentJournalHistoryReader.PageBytes + 5);
        CollectionAssert.AreEqual(new[] { "0", "1", "2", "3", "4" }, first.Entries.Concat(second.Entries)
            .Select(entry => ((AgentContentCompletedEvent)entry.Event).ContentId).ToArray());
    }

    [TestMethod]
    public async Task ReadPage_SeeksWithoutReadingEarlierHistory()
    {
        var prefix = new string(' ', 300_000) + "\n";
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(prefix + Line()));
        var page = await Read(stream, new("selected", stream.Length, 7, prefix.Length));
        Assert.AreEqual(1, page.Entries.Count);
        Assert.AreEqual((long)prefix.Length, page.Entries[0].Offset);
        Assert.IsTrue(stream.BytesRead < 4096);
        Assert.IsTrue(stream.ReadOffsets.All(offset => offset < 4 || offset >= prefix.Length - 1));
    }

    [TestMethod]
    public async Task ReadPage_PreservesCanonicalEventsAndSnapshotExclusions()
    {
        var snapshots = new[] { "local.sessionSummary", "local.sessionState", "codealta.sessionHeader", "codealta.sessionState" };
        var text = string.Concat(snapshots.Select(type => JsonSerializer.Serialize<AgentEvent>(
            new AgentRawEvent(new("p"), "runtime-other", DateTimeOffset.UnixEpoch, type, JsonSerializer.SerializeToElement(new { })),
            AgentJsonSerializerContext.Default.AgentEvent) + "\n"));
        text += Line() + Line(new AgentContentDeltaEvent(new("p"), "runtime-other", DateTimeOffset.UnixEpoch,
            new("run"), AgentContentKind.Reasoning, "content", null, "delta"));
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(text));
        var page = await Read(stream);
        Assert.AreEqual(2, page.Entries.Count);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(page.Entries[0].Event);
        Assert.IsInstanceOfType<AgentContentDeltaEvent>(page.Entries[1].Event);
        Assert.AreEqual("runtime-other", page.Entries[1].Event.SessionId);
    }

    [TestMethod]
    public async Task ReadPage_HandlesUtf8BomAndRecordBoundaries()
    {
        foreach (var ending in new[] { "\n", "\r\n", "" })
        {
            using var stream = new ProbeStream([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Line().TrimEnd('\n') + ending)]);
            var page = await Read(stream);
            Assert.AreEqual(3L, page.Entries.Single().Offset);
            Assert.IsNull(page.Next);
            Assert.IsFalse(page.TailOmitted);
            await Error("invalid_cursor", () => Read(stream, new("selected", stream.Length, 7, 3)));
        }
    }

    [TestMethod]
    public async Task ReadPage_ReportsUnsupportedEncodingAndOversizedRecords()
    {
        foreach (var bytes in new byte[][] { [0xff, 0xfe, 0, 0], [0xfe, 0xff, 0, 0], [0, 0, 0xfe, 0xff], Encoding.UTF8.GetBytes("{}\r{}") })
        {
            using var stream = new ProbeStream(bytes);
            await Error("unsupported_format", () => Read(stream));
        }
        using var oversized = new ProbeStream(Encoding.UTF8.GetBytes(new string('x', AgentJournalHistoryReader.RecordBytes + 1) + "\n"));
        await Error("record_too_large", () => Read(oversized));
    }

    [TestMethod]
    public async Task ReadPage_DistinguishesFinalTailFromInteriorCorruption()
    {
        using var tail = new ProbeStream(Encoding.UTF8.GetBytes(Line() + "{broken\n"));
        var page = await Read(tail);
        Assert.AreEqual(1, page.Entries.Count);
        Assert.IsTrue(page.TailOmitted);
        Assert.IsNull(page.Next);
        using var interior = new ProbeStream(Encoding.UTF8.GetBytes("{broken\n" + Line()));
        await Error("corrupt_record", () => Read(interior));
    }

    [TestMethod]
    public async Task ReadPage_ValidatesSessionBoundCursor()
    {
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(Line() + Line()));
        foreach (var cursor in new AgentSessionHistoryCursor[]
        {
            new("other", stream.Length, 7, 1), new("selected", stream.Length, 7, -1),
            new("selected", stream.Length, 7, stream.Length + 1), new("selected", stream.Length, 7, 1),
            new("selected", stream.Length, 7, 0)
        }) await Error("invalid_cursor", () => Read(stream, cursor));
    }

    [TestMethod]
    public async Task ReadPage_RejectsChangedRevision()
    {
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(Line() + Line()));
        await Error("history_changed", () => Read(stream, new("selected", stream.Length, 8, Line().Length)));
        var stamps = 0;
        await Error("history_changed", () => AgentJournalHistoryReader.ReadAsync(stream, "selected", null,
            () => new(stream.Length, ++stamps), CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadPage_ForwardsCancellationAndReadFailures()
    {
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(Line()));
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => AgentJournalHistoryReader.ReadAsync(stream, "selected", null,
            () => new(stream.Length, 7), canceled));
        var failure = new IOException("literal read failure");
        stream.Failure = failure;
        var actual = await Assert.ThrowsExactlyAsync<IOException>(() => Read(stream));
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    public async Task ReadPage_RejectsOutsideRootBeforeOpen()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\inert-copy\sessions" : "/inert-copy/sessions";
        var calls = 0;
        Task<int> Open(string path, CancellationToken token) { calls++; return Task.FromResult(1); }
        foreach (var path in new[] { root, root + "-other/file.jsonl", Path.Combine(root, "..", "original.jsonl"), "relative.jsonl" })
            await Error("outside_root", async () => { await AgentJournalHistoryReader.OpenContainedAsync(root, path, Open, CancellationToken.None); });
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, await AgentJournalHistoryReader.OpenContainedAsync(root, Path.Combine(root, "2026", "session.jsonl"), Open, CancellationToken.None));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ReadTail_LatestPromptAndOlderPagesStayBoundedAndOrdered()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "journal.jsonl");
            var lines = Enumerable.Range(0, 1605).Select(index => Line(new AgentContentCompletedEvent(new("p"),
                "runtime-other", DateTimeOffset.UnixEpoch, null, index == 1604 ? AgentContentKind.User : AgentContentKind.Assistant,
                index.ToString(System.Globalization.CultureInfo.InvariantCulture), null, index == 1604 ? "latest user prompt" : "old")));
            await File.WriteAllTextAsync(path, string.Concat(lines));
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            AgentSessionHistoryCursor? cursor = null;
            var ids = new List<string>();
            for (var pageNumber = 0; pageNumber < 17; pageNumber++)
            {
                var page = await ReadTail(file, cursor);
                Assert.IsTrue(page.Entries.Count <= 100);
                Assert.IsTrue(page.Entries.Zip(page.Entries.Skip(1)).All(pair => pair.First.Offset < pair.Second.Offset));
                ids.InsertRange(0, page.Entries.Select(entry => ((AgentContentCompletedEvent)entry.Event).ContentId));
                cursor = page.Next;
                if (pageNumber == 0)
                {
                    Assert.AreEqual("1604", ids[^1]);
                    Assert.AreEqual("latest user prompt", ((AgentContentCompletedEvent)page.Entries[^1].Event).Content);
                }
                if (cursor is null) break;
            }
            CollectionAssert.AreEqual(Enumerable.Range(0, 1605).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), ids);
            Assert.IsNull(cursor);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ReadTail_ValidatesCursorRevisionCorruptionAndReadFailures()
    {
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(Line() + Line() + Line()));
        var page = await ReadTail(stream);
        Assert.IsNull(page.Next);
        using var many = new ProbeStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Line(), 150))));
        var newest = await ReadTail(many);
        Assert.AreEqual(100, newest.Entries.Count);
        Assert.IsNotNull(newest.Next);
        Assert.IsTrue(many.BytesRead <= AgentJournalHistoryReader.PageBytes + 5);
        var reads = many.BytesRead;
        var older = await ReadTail(many, newest.Next);
        Assert.AreEqual(50, older.Entries.Count);
        Assert.IsTrue(older.Entries[^1].Offset < newest.Entries[0].Offset);
        Assert.IsTrue(many.BytesRead - reads <= AgentJournalHistoryReader.PageBytes + 5);
        using var window = new ProbeStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Line(), 2000))));
        await ReadTail(window);
        Assert.IsTrue(window.BytesRead <= AgentJournalHistoryReader.PageBytes + 5);
        Assert.IsTrue(window.MaximumRequest <= AgentJournalHistoryReader.PageBytes);
        await Error("invalid_cursor", () => ReadTail(many, newest.Next with { SessionId = "other" }));
        await Error("invalid_cursor", () => ReadTail(many, newest.Next with { Offset = newest.Next.Offset - 1 }));
        await Error("history_changed", () => ReadTail(many, newest.Next with { LastWriteUtcTicks = 8 }));
        var stamps = 0;
        await Error("history_changed", () => AgentJournalHistoryReader.ReadTailAsync(many, "selected", null,
            () => new(many.Length, ++stamps), CancellationToken.None));
        using var corrupt = new ProbeStream(Encoding.UTF8.GetBytes(Line() + "{broken\n" + Line()));
        await Error("corrupt_record", () => ReadTail(corrupt));
        using var tail = new ProbeStream(Encoding.UTF8.GetBytes(Line() + "{broken\n"));
        Assert.IsTrue((await ReadTail(tail)).TailOmitted);
        using var oversized = new ProbeStream(Encoding.UTF8.GetBytes(new string('x', AgentJournalHistoryReader.RecordBytes + 1) + "\n"));
        await Error("record_too_large", () => ReadTail(oversized));
        many.Failure = new IOException("literal read failure");
        Assert.AreSame(many.Failure, await Assert.ThrowsExactlyAsync<IOException>(() => ReadTail(many)));
    }

    [TestMethod]
    public async Task ReadTail_PreservesFormatAndOnlyOmitsMalformedFinalRecord()
    {
        using var formatted = new ProbeStream([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Line().Replace("\n", "\r\n") + "  \r\n" + Line().TrimEnd('\n'))]);
        var page = await ReadTail(formatted);
        Assert.AreEqual(2, page.Entries.Count);
        Assert.AreEqual(3L, page.Entries[0].Offset);
        Assert.IsNull(page.Next);
        Assert.IsFalse(page.TailOmitted);
        using var encoding = new ProbeStream([0xff, 0xfe, 0, 0]);
        await Error("unsupported_format", () => ReadTail(encoding));
        using var invalidUtf8 = new ProbeStream([0xff]);
        await Error("unsupported_format", () => ReadTail(invalidUtf8));
        using var interior = new ProbeStream(Encoding.UTF8.GetBytes("{broken\n" + string.Concat(Enumerable.Repeat(Line(), 101))));
        var recent = await ReadTail(interior);
        Assert.IsNotNull(recent.Next);
        await Error("corrupt_record", () => ReadTail(interior, recent.Next));
        using var large = new ProbeStream(Encoding.UTF8.GetBytes(new string(' ', AgentJournalHistoryReader.PageBytes + 20) + "\n" + Line()));
        var latest = await ReadTail(large);
        Assert.AreEqual(1, latest.Entries.Count);
        await Error("record_too_large", () => ReadTail(large, latest.Next));
    }

    private static string Line(AgentEvent? value = null) => JsonSerializer.Serialize(value ??
        new AgentContentCompletedEvent(new("p"), "runtime-other", DateTimeOffset.UnixEpoch, null,
            AgentContentKind.Assistant, "content", null, "hello"), AgentJsonSerializerContext.Default.AgentEvent) + "\n";

    private static Task<AgentSessionHistoryPage> Read(Stream stream, AgentSessionHistoryCursor? cursor = null) =>
        AgentJournalHistoryReader.ReadAsync(stream, "selected", cursor, () => new(stream.Length, 7), CancellationToken.None);

    private static Task<AgentSessionHistoryPage> ReadTail(Stream stream, AgentSessionHistoryCursor? cursor = null) =>
        AgentJournalHistoryReader.ReadTailAsync(stream, "selected", cursor, () => new(stream.Length, 7), CancellationToken.None);

    private static async Task Error(string code, Func<Task> action) =>
        Assert.AreEqual(code, (await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(action)).Code);

    private sealed class ProbeStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal long BytesRead { get; private set; }
        internal int MaximumRequest { get; private set; }
        internal List<long> ReadOffsets { get; } = [];
        internal IOException? Failure { get; set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) return ValueTask.FromException<int>(Failure);
            ReadOffsets.Add(Position);
            MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            var read = Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
