using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>Latest-notes reads must not rescan an unchanged journal or hold its gate while scanning.</summary>
[TestClass]
public sealed class SessionNotesReadCostTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [TestMethod]
    public async Task UnchangedJournal_IsAnsweredWithoutOpeningTheJournalAgain()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var note = Note("retained");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [note, Update("after")]);
        Assert.AreEqual(note, await fixture.Store.ReadLatestNotesAsync("session"));

        // Stores are created per read in production and share only the journal file owner.
        var other = new FileSystemAgentSessionStore(fixture.Layout, fixture.JournalFile);
        await using (new FileStream(fixture.Journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.AreEqual(note, await fixture.Store.ReadLatestNotesAsync("session"));
            Assert.AreEqual(note, await other.ReadLatestNotesContainedAsync("session"));
        }
    }

    [TestMethod]
    public async Task GrownJournal_IsReadOnlyPastTheScannedPrefix()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var first = Note("first");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session",
            [.. Enumerable.Range(0, 200).Select(index => Update("before " + index)), first, Update("after")]);
        var streams = new List<RecordingStream>();
        Task<Stream> Open(string path, CancellationToken token)
        {
            streams.Add(new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)));
            return Task.FromResult<Stream>(streams[^1]);
        }

        var scannedLength = new FileInfo(fixture.Journal).Length;
        Assert.AreEqual(first, (await fixture.JournalFile.ReadLatestNotesAsync(fixture.Journal, Open, default)).Latest);
        Assert.AreEqual(0, streams.Single().LowestRead);

        var second = Note("second");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Update("more"), second, Update("last")]);
        var appended = new FileInfo(fixture.Journal).Length - scannedLength;
        Assert.AreEqual(second, (await fixture.JournalFile.ReadLatestNotesAsync(fixture.Journal, Open, default)).Latest);
        Assert.AreEqual(2, streams.Count);
        Assert.IsTrue(streams[1].LowestRead >= scannedLength - AgentJournalNotesReader.GuardBytes, "The scanned prefix was read again.");
        Assert.IsTrue(streams[1].BytesRead <= appended + (2 * AgentJournalNotesReader.GuardBytes), "More than the appended records was read.");

        Assert.AreEqual(second, (await fixture.JournalFile.ReadLatestNotesAsync(fixture.Journal, Open, default)).Latest);
        Assert.AreEqual(2, streams.Count, "An unchanged journal was opened again.");
    }

    [TestMethod]
    public async Task Scan_DoesNotDelayHistoryPagesOrAppendsForTheSameJournal()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var first = Note("first");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [first]);
        var scanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = fixture.JournalFile.ReadLatestNotesAsync(fixture.Journal, async (path, token) =>
        {
            scanning.SetResult();
            await resume.Task.WaitAsync(token);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }, default);
        await scanning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = Note("second");
        try
        {
            var page = await fixture.Store.ReadTimelinePageAsync("session", null, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(first, page.Entries.Select(entry => entry.Event).OfType<AgentNotesEvent>().Single());
            await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [second]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(read.IsCompleted);
        }
        finally
        {
            resume.SetResult();
        }

        // The scan answers for the journal as it was when the read was admitted.
        Assert.AreEqual(first, (await read.WaitAsync(TimeSpan.FromSeconds(5))).Latest);
        Assert.AreEqual(second, await fixture.Store.ReadLatestNotesAsync("session"));
    }

    [TestMethod]
    public async Task ResumedRead_KeepsTailToleranceAndRejectsMalformedInterior()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var note = Note("retained");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [note]);
        Assert.AreEqual(note, await fixture.Store.ReadLatestNotesAsync("session"));

        await File.AppendAllTextAsync(fixture.Journal, "{broken", Utf8);
        Assert.AreEqual(note, await fixture.Store.ReadLatestNotesAsync("session"));
        Assert.AreEqual(note, await fixture.Store.ReadLatestNotesAsync("session"));

        await File.AppendAllTextAsync(fixture.Journal, "\n" + Note("hidden").ToJson() + "\n", Utf8);
        await Assert.ThrowsExactlyAsync<JsonException>(() => fixture.Store.ReadLatestNotesAsync("session"));
        await Assert.ThrowsExactlyAsync<JsonException>(() => fixture.Store.ReadLatestNotesAsync("session"));
    }

    [TestMethod]
    public async Task ResumedRead_StillRejectsNotesWithoutMarkdown()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Note("retained")]);
        Assert.AreEqual("retained", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);

        // Deliberately corrupt persisted data; the production event contract is non-nullable.
        await File.AppendAllTextAsync(fixture.Journal, Note(null!).ToJson() + "\n", Utf8);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.ReadLatestNotesAsync("session"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.ReadLatestNotesAsync("session"));
    }

    [TestMethod]
    public async Task RewrittenJournal_IsReadFromTheStart()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Note("old"), Update("after")]);
        Assert.AreEqual("old", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);
        var original = await File.ReadAllTextAsync(fixture.Journal, Utf8);

        // Longer, with every record moved by an inserted first line.
        await File.WriteAllTextAsync(fixture.Journal, Update("inserted").ToJson() + "\n" + original.Replace("old", "moved"), Utf8);
        Assert.AreEqual("moved", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);

        // Same length, later write time.
        var same = (await File.ReadAllTextAsync(fixture.Journal, Utf8)).Replace("moved", "equal");
        await File.WriteAllTextAsync(fixture.Journal, same, Utf8);
        File.SetLastWriteTimeUtc(fixture.Journal, File.GetLastWriteTimeUtc(fixture.Journal).AddSeconds(2));
        Assert.AreEqual("equal", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);

        // Shorter.
        await File.WriteAllTextAsync(fixture.Journal, original.Replace("old", "cut"), Utf8);
        Assert.AreEqual("cut", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);

        // Re-encoded with a byte order mark only the tolerant text reader decodes.
        await File.WriteAllTextAsync(fixture.Journal, original.Replace("old", "wide"), Encoding.Unicode);
        Assert.AreEqual("wide", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);
    }

    [TestMethod]
    public async Task DeletedJournal_IsReportedMissingInsteadOfAnsweredFromItsScan()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Note("retained")]);
        Assert.AreEqual("retained", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);
        var journal = fixture.Journal;

        Assert.IsTrue(await fixture.Store.DeleteSessionAsync("session"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.ReadLatestNotesAsync("session"));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fixture.JournalFile.ReadLatestNotesAsync(journal,
            static (_, _) => throw new AssertFailedException("A missing journal must not be opened."), default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EveryPrefixAndItsContinuation_MatchTheTolerantTextReader(bool byteOrderMark)
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var journal = fixture.Journal;
        var header = await File.ReadAllTextAsync(journal, Utf8);
        var content = (byteOrderMark ? Encoding.UTF8.GetPreamble() : [])
            .Concat(Utf8.GetBytes(header
                + Update("one").ToJson() + "\r\n"
                + Note("first").ToJson() + "\n"
                + "\r\n"
                + "  \n"
                + Update("two").ToJson() + "\r"
                + Note("# Ω 😀\r\n  ").ToJson() + "\r\n"
                + Update("three").ToJson() + "\n"
                + Note("", AgentNotesUpdateKind.Cleared).ToJson()))
            .ToArray();

        for (var length = 0; length <= content.Length; length++)
        {
            // A new journal owner per prefix: nothing is remembered from the previous length.
            var store = new FileSystemAgentSessionStore(fixture.Layout, new AgentSessionJournalFile());
            await File.WriteAllBytesAsync(journal, content[..length]);
            Assert.AreEqual(await Expected(store), await store.ReadLatestNotesAsync("session"), "Prefix " + length);
            await File.WriteAllBytesAsync(journal, content);
            Assert.AreEqual(await Expected(store), await store.ReadLatestNotesAsync("session"), "Continuation of " + length);
        }

        static async Task<AgentNotesEvent?> Expected(FileSystemAgentSessionStore store)
            => (await store.ReadEventsAsync("session")).OfType<AgentNotesEvent>().LastOrDefault();
    }

    private static AgentNotesEvent Note(string markdown, AgentNotesUpdateKind kind = AgentNotesUpdateKind.Set)
        => new(new("notes-provider"), "session", DateTimeOffset.UnixEpoch, null, kind, markdown);

    private static AgentSessionUpdateEvent Update(string message)
        => new(new("notes-provider"), "session", DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.ModelChanged, message);

    private sealed class RecordingStream(FileStream inner) : Stream
    {
        internal long LowestRead { get; private set; } = long.MaxValue;
        internal long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Record(inner.Position, inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var position = inner.Position;
            return Record(position, await inner.ReadAsync(buffer, cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Record(long position, int count)
        {
            if (count > 0) LowestRead = Math.Min(LowestRead, position);
            BytesRead += count;
            return count;
        }
    }

    private sealed class Fixture
    {
        internal required AgentRuntimePathLayout Layout { get; init; }
        internal required AgentSessionJournalFile JournalFile { get; init; }
        internal required FileSystemAgentSessionStore Store { get; init; }
        internal string Journal => Layout.GetSessionFilePath("session", DateTimeOffset.UnixEpoch);

        internal static async Task<Fixture> CreateAsync(string root)
        {
            var layout = new AgentRuntimePathLayout(root);
            var journalFile = new AgentSessionJournalFile();
            var fixture = new Fixture { Layout = layout, JournalFile = journalFile, Store = new(layout, journalFile) };
            await fixture.Store.UpsertSessionAsync(new()
            {
                SessionId = "session", ProviderId = new("notes-provider"), ProviderKey = "notes-provider", ProtocolFamily = "fixture",
                WorkingDirectory = root, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Title = "Notes fixture",
            });
            return fixture;
        }
    }
}
