using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>The usage records of a journal, read in one pass up to the length it has when the read starts.</summary>
[TestClass]
public sealed class AgentSessionUsageStoreTests
{
    private const string Source = "0199a000-0000-7000-8000-000000000011";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00");
    private static readonly ModelProviderId Provider = ModelProviderIds.OpenAIResponses;

    [TestMethod]
    public async Task ReadUsageUpdates_ListsTheOperationsOfAJournal_InItsOrder()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        Assert.AreEqual(0, (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)).Count, "A journal without usage has none.");

        var first = new AgentOperationUsageSnapshot("gpt-5.4", InputTokens: 100, OutputTokens: 20, CachedInputTokens: 50, Cost: .25, DurationMs: 1200, CostUnit: "AI credits");
        var second = new AgentOperationUsageSnapshot(InputTokens: 10, OutputTokens: 30, CacheReadTokens: 90, CacheWriteTokens: 5, ReasoningTokens: 7, Initiator: "compaction");
        var third = new AgentOperationUsageSnapshot(OutputTokens: 1, Label: "é\"\\\n\U0001F600");
        await AppendAsync(store,
            Content("run-1", AgentContentKind.User, "First prompt"),
            Usage("run-1", first),
            // A usage record that only reports the context window, then the snapshots of the end of a run and of a compaction.
            new AgentSessionUpdateEvent(Provider, Source, Start, new("run-1"), AgentSessionUpdateKind.UsageUpdated, null, Usage: new(Window: new(10, 100, 2))),
            new AgentSessionUpdateEvent(Provider, Source, Start, new("run-1"), AgentSessionUpdateKind.Idle, null, Usage: new(LastOperation: first)),
            new AgentSessionUpdateEvent(Provider, Source, Start, null, AgentSessionUpdateKind.CompactionCompleted, null, Usage: new(LastOperation: second)),
            Usage(null, second),
            Content("run-2", AgentContentKind.Assistant, "{\"$type\":\"sessionUpdate\",\"kind\":\"UsageUpdated\"} is only text here"),
            Usage("run-2", third)).ConfigureAwait(false);

        var updates = await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false);

        CollectionAssert.AreEqual(new AgentSessionUsageUpdate[] { new("run-1", first), new(null, second), new("run-2", third) }, updates.ToArray());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.ReadUsageUpdatesAsync(" ")).ConfigureAwait(false);
        var missing = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => store.ReadUsageUpdatesAsync("0199a000-0000-7000-8000-0000000000ff")).ConfigureAwait(false);
        Assert.AreEqual("missing_session", missing.Code);
    }

    [TestMethod]
    public async Task ReadUsageUpdates_PassesARecordOfSeveralMegabytes_WithoutLoadingIt()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        // Larger than the 8 MiB a single record may have for the timeline, which fails its pages.
        var large = new string('x', AgentJournalHistoryReader.TimelineRecordBytes + 1024 * 1024);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 1)),
            new AgentRawEvent(Provider, Source, Start, "local.toolMessage", JsonSerializer.SerializeToElement(new { output = large }), new("run-1")),
            Usage("run-1", new(InputTokens: 2)),
            // A record cut by more than one read, whose start alone says that it is no usage record.
            Content("run-1", AgentContentKind.Assistant, new string('y', 300 * 1024)),
            Usage("run-2", new(InputTokens: 3))).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(async () =>
        {
            AgentSessionHistoryCursor? cursor = null;
            do
            {
                cursor = (await store.ReadTimelinePageAsync(Source, cursor, CancellationToken.None).ConfigureAwait(false)).Next;
            }
            while (cursor is not null);
        }).ConfigureAwait(false);

        var updates = await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false);

        CollectionAssert.AreEqual(new long?[] { 1, 2, 3 }, updates.Select(static update => update.Operation.InputTokens).ToArray());
    }

    [TestMethod]
    public async Task ReadUsageUpdates_ReadsWindowsLineEnds_AndLeavesAnUnfinishedLastRecord()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 1, Cost: 2)), Content("run-1", AgentContentKind.Assistant, "Answer"),
            Usage("run-1", new(InputTokens: 3))).ConfigureAwait(false);
        var path = JournalPath(temp.Path);
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text).ConfigureAwait(false);

        var updates = await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false);
        CollectionAssert.AreEqual(new long?[] { 1, 3 }, updates.Select(static update => update.Operation.InputTokens).ToArray());
        Assert.AreEqual(2d, updates[0].Operation.Cost);

        // A record without its line end is one that is being written: whole or cut, it is not listed and fails nothing.
        var unfinished = Usage("run-2", new(InputTokens: 5)).ToJson();
        await File.AppendAllTextAsync(path, unfinished).ConfigureAwait(false);
        Assert.AreEqual(2, (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)).Count);
        await File.WriteAllTextAsync(path, text + unfinished[..(unfinished.Length / 2)]).ConfigureAwait(false);
        Assert.AreEqual(2, (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)).Count);
        // Once it has its line end it is a record like the others.
        await File.WriteAllTextAsync(path, text + unfinished + "\r\n").ConfigureAwait(false);
        Assert.AreEqual(5, (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false))[^1].Operation.InputTokens);
    }

    [TestMethod]
    public async Task ReadUsageUpdates_StopsAtTheLengthOfItsStart_WhileTheJournalGrows()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 1)), Content("run-1", AgentContentKind.Assistant, new string('y', 300 * 1024)),
            Usage("run-1", new(InputTokens: 2))).ConfigureAwait(false);
        var path = JournalPath(temp.Path);
        var length = new FileInfo(path).Length;

        // The run appends as soon as the scan has read its first bytes: what it appends lies after the captured length.
        List<AgentSessionUsageUpdate> scanned;
        await using (var stream = new HookedStream(path, readSize: int.MaxValue))
        {
            stream.AfterFirstRead = () => AppendAsync(store, Usage("run-1", new(InputTokens: 4)), Usage("run-1", new(InputTokens: 8))).GetAwaiter().GetResult();
            scanned = await AgentJournalUsageReader.ReadAsync(stream, length, CancellationToken.None).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(new long?[] { 1, 2 }, scanned.Select(static update => update.Operation.InputTokens).ToArray());
        Assert.IsTrue(new FileInfo(path).Length > length);
        CollectionAssert.AreEqual(new long?[] { 1, 2, 4, 8 },
            (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)).Select(static update => update.Operation.InputTokens).ToArray());

        // Reads of the store while a run appends: each one lists a prefix of what is written, and none fails.
        using var stop = new CancellationTokenSource();
        var written = 4;
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await AppendAsync(store, Usage("run-2", new(InputTokens: 16)), Content("run-2", AgentContentKind.Assistant, "Answer")).ConfigureAwait(false);
                Interlocked.Increment(ref written);
            }
        });
        try
        {
            var last = 4;
            for (var read = 0; read < 40; read++)
            {
                var before = Volatile.Read(ref written);
                var count = (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)).Count;
                Assert.IsTrue(count >= before && count >= last && count <= Volatile.Read(ref written) + 1, $"{before} <= {count}");
                last = count;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await writer.ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ReadUsageUpdates_ReadsTheSame_HoweverTheReadsCutTheRecords()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        var operations = Enumerable.Range(1, 12).Select(static index => new AgentOperationUsageSnapshot("model-" + index, InputTokens: index,
            Label: new string('l', index * 37), CostUnit: index % 2 == 0 ? "AI credits" : null)).ToArray();
        foreach (var operation in operations)
        {
            await AppendAsync(store, Content("run", AgentContentKind.User, new string('p', (int)operation.InputTokens!.Value * 23)),
                Usage(operation.InputTokens % 3 == 0 ? null : "run", operation)).ConfigureAwait(false);
        }

        var path = JournalPath(temp.Path);
        var length = new FileInfo(path).Length;
        foreach (var readSize in new[] { 1, 7, 159, 160, 161, 4096 })
        {
            await using var stream = new HookedStream(path, readSize);
            var scanned = await AgentJournalUsageReader.ReadAsync(stream, length, CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(operations, scanned.Select(static update => update.Operation).ToArray(), $"Reads of {readSize} bytes.");
        }
    }

    [TestMethod]
    public async Task ReadUsageUpdates_FailsOnAUsageRecordItCannotRead_RatherThanListingAPart()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 1))).ConfigureAwait(false);
        var path = JournalPath(temp.Path);
        var sound = await File.ReadAllBytesAsync(path).ConfigureAwait(false);

        await File.AppendAllTextAsync(path, "{\"$type\":\"sessionUpdate\",\"kind\":\"UsageUpdated\",\"usage\":{\"lastOperation\":{\"inputTokens\":}}}\n").ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 2))).ConfigureAwait(false);
        var corrupt = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => store.ReadUsageUpdatesAsync(Source)).ConfigureAwait(false);
        Assert.AreEqual("corrupt_record", corrupt.Code);

        // A corrupt record of another kind is none of the usage: the timeline reports it, this read passes it.
        await File.WriteAllBytesAsync(path, sound).ConfigureAwait(false);
        await File.AppendAllTextAsync(path, "{\"$type\":\"contentCompleted\",\"kind\":\"Assistant\",\"content\":\n").ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 2))).ConfigureAwait(false);
        Assert.AreEqual(2, (await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false)).Count);

        await File.WriteAllBytesAsync(path, sound).ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 2, Label: new string('l', AgentJournalHistoryReader.TimelineRecordBytes))),
            Usage("run-1", new(InputTokens: 3))).ConfigureAwait(false);
        var large = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => store.ReadUsageUpdatesAsync(Source)).ConfigureAwait(false);
        Assert.AreEqual("record_too_large", large.Code);

        await File.WriteAllBytesAsync(path, [0xff, 0xfe, .. Encoding.Unicode.GetBytes("{}\n")]).ConfigureAwait(false);
        var format = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => store.ReadUsageUpdatesAsync(Source)).ConfigureAwait(false);
        Assert.AreEqual("unsupported_format", format.Code);
    }

    [TestMethod]
    public async Task ReadUsageUpdates_IsCanceled_BeforeAndDuringTheScan()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        await AppendAsync(store, Usage("run-1", new(InputTokens: 1)), Content("run-1", AgentContentKind.Assistant, new string('y', 600 * 1024)),
            Usage("run-1", new(InputTokens: 2))).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadUsageUpdatesAsync(Source, new CancellationToken(true))).ConfigureAwait(false);

        var path = JournalPath(temp.Path);
        using var cancellation = new CancellationTokenSource();
        await using var stream = new HookedStream(path, readSize: int.MaxValue);
        stream.AfterFirstRead = cancellation.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => AgentJournalUsageReader.ReadAsync(stream, new FileInfo(path).Length, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(1, stream.Reads, "The scan stops at the read after the cancellation, not at the end of the journal.");
    }

    [TestMethod]
    public async Task ReadUsageUpdates_LeavesOutWhatAForkCopiedFromItsSource()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        var forkedAt = Start.AddHours(1);
        var summary = await store.GetSessionSummaryAsync(Source).ConfigureAwait(false);
        await store.UpsertSessionAsync(summary! with
        {
            ForkedFrom = new AgentSessionForkOrigin { SessionId = "source", RootSessionId = "source", ForkedAt = forkedAt, CountedFrom = forkedAt },
        }).ConfigureAwait(false);
        var own = new AgentOperationUsageSnapshot(InputTokens: 7, Cost: .5);
        AgentSessionUpdateEvent At(DateTimeOffset time, AgentOperationUsageSnapshot operation)
            => new(Provider, Source, time, new AgentRunId("run"), AgentSessionUpdateKind.UsageUpdated, null, Usage: new(LastOperation: operation));
        // The usage of the source, copied with its turns, then the usage of the fork itself from the time it was made.
        await AppendAsync(store, At(Start, new(InputTokens: 1000, Cost: 3)), At(forkedAt.AddTicks(-1), new(InputTokens: 2000)),
            At(forkedAt, own), At(forkedAt.AddMinutes(5), own)).ConfigureAwait(false);

        var updates = await store.ReadUsageUpdatesAsync(Source).ConfigureAwait(false);

        CollectionAssert.AreEqual(new AgentSessionUsageUpdate[] { new("run", own), new("run", own) }, updates.ToArray());
    }

    private static string JournalPath(string root) => new AgentRuntimePathLayout(root).GetSessionFilePath(Source, Start);

    private static AgentSessionUpdateEvent Usage(string? run, AgentOperationUsageSnapshot operation)
        => new(Provider, Source, Start, run is null ? null : new AgentRunId(run), AgentSessionUpdateKind.UsageUpdated, null,
            Usage: new(Window: new(10, 100, 2), LastOperation: operation, Scope: AgentUsageScope.LastOperation, Source: AgentUsageSource.ProviderUsage));

    private static AgentContentCompletedEvent Content(string run, AgentContentKind kind, string text)
        => new(Provider, Source, Start, new AgentRunId(run), kind, $"content:{Guid.CreateVersion7()}", null, text);

    private static Task AppendAsync(FileSystemAgentSessionStore store, params AgentEvent[] events)
        => store.AppendEventsAsync("openai", "openai", Source, events);

    private static async Task CreateSessionAsync(FileSystemAgentSessionStore store)
    {
        await store.UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = Source,
            ProviderId = Provider,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "/repo/sample",
            Title = "Sample session",
            CreatedAt = Start,
            UpdatedAt = Start,
        }).ConfigureAwait(false);
        await store.UpsertStateAsync(new AgentSessionState { SessionId = Source, ProtocolFamily = "openai", ProviderKey = "openai", UpdatedAt = Start }).ConfigureAwait(false);
    }

    // The journal as a read sees it, with reads of a chosen size and a hook after the first of them.
    private sealed class HookedStream(string path, int readSize) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        internal Action? AfterFirstRead { get; set; }

        internal int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _file.Length;

        public override long Position
        {
            get => _file.Position;
            set => _file.Position = value;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _file.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], cancellationToken).ConfigureAwait(false);
            if (Reads++ == 0)
            {
                AfterFirstRead?.Invoke();
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override async ValueTask DisposeAsync()
        {
            await _file.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
