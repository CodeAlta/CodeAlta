using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>A system prompt record of a journal, read with the prompt it replaced: the prompt record before it.</summary>
[TestClass]
public sealed class AgentSessionPromptStoreTests
{
    private const string Source = "0199a000-0000-7000-8000-000000000021";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00");
    private static readonly ModelProviderId Provider = ModelProviderIds.OpenAIResponses;

    [TestMethod]
    public async Task ReadSystemPrompt_GivesTheRecordWhole_WithThePromptItReplaced()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        // Longer than a history row may be, with what a line scan must not trip on: quotes, line ends, other scripts.
        var system = "You are \"CodeAlta\".\r\n" + string.Concat(Enumerable.Range(0, 900).Select(static index => $"- Rule {index}: ünïcödé 😀\n"));
        var first = Prompt("hash-1", "initial", system, "# Agent Prompt\n\nFirst.");
        var second = Prompt("hash-2", "changed", system, "# Agent Prompt\n\nSecond.");
        var third = Prompt("hash-3", "changed", system + "- One more rule\n", "# Agent Prompt\n\nSecond.");
        await AppendAsync(store, first, Content("run-1", AgentContentKind.User, "First prompt"),
            // A message that only quotes the start of a prompt record is none.
            Content("run-1", AgentContentKind.Assistant, "{\"$type\":\"system_prompt\",\"reason\":\"changed\"} is only text here"),
            second, Content("run-2", AgentContentKind.User, "Second prompt"), third,
            Content("run-3", AgentContentKind.Assistant, "Answer")).ConfigureAwait(false);
        var offsets = PromptOffsets(JournalPath(temp.Path));
        Assert.AreEqual(3, offsets.Count);

        var initial = await store.ReadSystemPromptAsync(Source, offsets[0]).ConfigureAwait(false);
        Assert.AreEqual(("hash-1", system, first.DeveloperInstructions, first.Statistics, first.AgentPromptUsage),
            (initial.Prompt.EffectivePromptHash, initial.Prompt.SystemMessage, initial.Prompt.DeveloperInstructions, initial.Prompt.Statistics, initial.Prompt.AgentPromptUsage));
        Assert.IsNull(initial.Previous, "The first prompt of a session replaced none.");
        Assert.IsNull(initial.PreviousOffset);

        var changed = await store.ReadSystemPromptAsync(Source, offsets[1]).ConfigureAwait(false);
        Assert.AreEqual("hash-2", changed.Prompt.EffectivePromptHash);
        Assert.AreEqual(system, changed.Prompt.SystemMessage, "The text is whole.");
        Assert.AreEqual("hash-1", changed.Previous!.EffectivePromptHash);
        Assert.AreEqual(offsets[0], changed.PreviousOffset);

        var last = await store.ReadSystemPromptAsync(Source, offsets[2]).ConfigureAwait(false);
        Assert.AreEqual(system + "- One more rule\n", last.Prompt.SystemMessage);
        Assert.AreEqual("hash-2", last.Previous!.EffectivePromptHash, "The prompt before, not the first of the session.");
        Assert.AreEqual(system, last.Previous.SystemMessage);
        Assert.AreEqual(offsets[1], last.PreviousOffset);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.ReadSystemPromptAsync(" ", offsets[0])).ConfigureAwait(false);
        var missing = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(
            () => store.ReadSystemPromptAsync("0199a000-0000-7000-8000-0000000000ff", offsets[0])).ConfigureAwait(false);
        Assert.AreEqual("missing_session", missing.Code);
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadSystemPromptAsync(Source, offsets[2], new CancellationToken(true))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReadSystemPrompt_RefusesAnOffsetThatStartsNoPromptRecord()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        await AppendAsync(store, Prompt("hash-1", "initial", "System", "Developer"), Content("run-1", AgentContentKind.User, "A prompt"),
            Prompt("hash-2", "changed", "System", "Other")).ConfigureAwait(false);
        var path = JournalPath(temp.Path);
        var offsets = PromptOffsets(path);
        var other = LineOffsets(path).First(offset => offset > offsets[0] && offset != offsets[1]);

        // The record of a message, the middle of a prompt record, the end of the journal and beyond.
        foreach (var offset in new[] { other, offsets[1] + 1, offsets[1] + 20, new FileInfo(path).Length, long.MaxValue, -1 })
        {
            var refused = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(() => store.ReadSystemPromptAsync(Source, offset)).ConfigureAwait(false);
            Assert.AreEqual("invalid_cursor", refused.Code, $"Offset {offset}.");
        }
    }

    [TestMethod]
    public async Task ReadSystemPrompt_PassesARecordOfSeveralMegabytes_WithoutLoadingIt()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        // Larger than the 8 MiB a single record may have for the timeline, which fails its pages.
        var large = new string('x', AgentJournalHistoryReader.TimelineRecordBytes + 1024 * 1024);
        await AppendAsync(store, Prompt("hash-1", "initial", "System", "Developer"),
            new AgentRawEvent(Provider, Source, Start, "local.toolMessage", JsonSerializer.SerializeToElement(new { output = large }), new("run-1")),
            // A record cut by more than one read, whose start alone says that it is no prompt record.
            Content("run-1", AgentContentKind.Assistant, new string('y', 300 * 1024)),
            Prompt("hash-2", "changed", "System", "Other")).ConfigureAwait(false);
        var path = JournalPath(temp.Path);
        var offsets = PromptOffsets(path);
        var loaded = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(
            () => store.ReadHistoryRecordAsync(Source, LineOffsets(path).First(offset => offset > offsets[0]), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("record_too_large", loaded.Code, "Loading the record between the two prompts fails.");

        var read = await store.ReadSystemPromptAsync(Source, offsets[1]).ConfigureAwait(false);

        Assert.AreEqual("hash-2", read.Prompt.EffectivePromptHash);
        Assert.AreEqual("hash-1", read.Previous!.EffectivePromptHash);
        Assert.AreEqual(offsets[0], read.PreviousOffset);

        // The scan holds one buffer: it never asks the journal for more than that at once.
        await using var stream = new MeasuredStream(path, readSize: int.MaxValue);
        Assert.AreEqual(offsets[0], await AgentJournalHistoryReader.FindPreviousSystemPromptAsync(stream, offsets[1], CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(stream.LargestRead <= 1024 * 1024, $"A read of {stream.LargestRead} bytes.");
    }

    [TestMethod]
    public async Task FindPreviousSystemPrompt_FindsTheSame_HoweverTheReadsCutTheRecords()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        for (var index = 1; index <= 6; index++)
        {
            await AppendAsync(store, Content("run", AgentContentKind.User, new string('p', index * 41)),
                Prompt("hash-" + index, index == 1 ? "initial" : "changed", new string('s', index * 67), "Developer")).ConfigureAwait(false);
        }

        var path = JournalPath(temp.Path);
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        // Journals written on Windows end their lines with CR LF.
        await File.WriteAllTextAsync(path, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).ConfigureAwait(false);
        var offsets = PromptOffsets(path);
        Assert.AreEqual(6, offsets.Count);
        foreach (var readSize in new[] { 1, 7, 159, 160, 161, 4096 })
        {
            await using var stream = new MeasuredStream(path, readSize);
            Assert.AreEqual(-1, await AgentJournalHistoryReader.FindPreviousSystemPromptAsync(stream, offsets[0], CancellationToken.None).ConfigureAwait(false));
            for (var index = 1; index < offsets.Count; index++)
            {
                Assert.AreEqual(offsets[index - 1], await AgentJournalHistoryReader.FindPreviousSystemPromptAsync(stream, offsets[index], CancellationToken.None).ConfigureAwait(false),
                    $"Reads of {readSize} bytes, prompt {index}.");
            }

            var read = await AgentJournalHistoryReader.ReadSystemPromptAsync(stream, offsets[5], CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(("hash-6", "hash-5"), (read.Prompt.EffectivePromptHash, read.Previous!.EffectivePromptHash));
        }

        using var cancellation = new CancellationTokenSource();
        await using var canceled = new MeasuredStream(path, readSize: 64) { AfterFirstRead = cancellation.Cancel };
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AgentJournalHistoryReader.FindPreviousSystemPromptAsync(canceled, offsets[5], cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(1, canceled.Reads, "The scan stops at the read after the cancellation, not at the offset.");
    }

    [TestMethod]
    public async Task ReadSystemPrompt_LeavesAPromptWithoutTheOneBefore_WhenThatOneCannotBeRead()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        await CreateSessionAsync(store).ConfigureAwait(false);
        var path = JournalPath(temp.Path);
        // A prompt record that is not one the serializer wrote, then the prompt that replaced it.
        await File.AppendAllTextAsync(path, "{\"$type\":\"system_prompt\",\"reason\":\n").ConfigureAwait(false);
        await AppendAsync(store, Prompt("hash-2", "changed", "System", "Developer")).ConfigureAwait(false);

        var read = await store.ReadSystemPromptAsync(Source, PromptOffsets(path)[^1]).ConfigureAwait(false);

        Assert.AreEqual("hash-2", read.Prompt.EffectivePromptHash);
        Assert.IsNull(read.Previous);
        Assert.IsNull(read.PreviousOffset);
    }

    private static string JournalPath(string root) => new AgentRuntimePathLayout(root).GetSessionFilePath(Source, Start);

    // Where the lines of the journal start, and those of its prompt records.
    private static List<long> LineOffsets(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offsets = new List<long>();
        for (var start = 0; start < bytes.Length;)
        {
            offsets.Add(start);
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            start = end < 0 ? bytes.Length : end + 1;
        }

        return offsets;
    }

    private static List<long> PromptOffsets(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var marker = "{\"$type\":\"system_prompt\""u8.ToArray();
        return LineOffsets(path).Where(offset => bytes.AsSpan((int)offset).StartsWith(marker)).ToList();
    }

    private static AgentSystemPromptEvent Prompt(string hash, string change, string system, string developer)
        => new(Provider, Source, Start, new AgentRunId("run"), change == "initial" ? "session_start" : "changed", hash, "default", system, developer,
            new("native-system-and-developer", true, false), null, new(1, 2, 3, system.Length, developer.Length),
            change == "initial" ? new("initial", ["system", "developer"], [], []) : new("changed", [], [], ["effective_prompt"]),
            new("default", "Default", "prompts/default.prompt.md"));

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

    // The journal as a read sees it, with reads of a chosen size, the largest one it was asked for and a hook after the first.
    private sealed class MeasuredStream(string path, int readSize) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        internal Action? AfterFirstRead { get; init; }

        internal int Reads { get; private set; }

        internal int LargestRead { get; private set; }

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
            LargestRead = Math.Max(LargestRead, buffer.Length);
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
