using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>Latest-notes reads and notes appends must not rescan records already scanned or hold the journal gate while scanning.</summary>
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
        var content = await MixedTerminatorJournalAsync(journal, byteOrderMark);

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

    [TestMethod]
    public async Task NotesAppend_ValidatesOnlyPastTheScannedPrefix()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var first = Note("first");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session",
            [.. Enumerable.Range(0, 200).Select(index => Update("before " + index)), first, Update("after")]);
        Assert.AreEqual(first, await fixture.Store.ReadLatestNotesAsync("session"));
        var streams = new List<RecordingStream>();
        Task AppendAsync(AgentNotesEvent note)
        {
            streams.Clear();
            return fixture.JournalFile.AppendNotesLineAsync(fixture.Journal, note.ToJson(), Utf8,
                (path, _) => Task.FromResult<Stream>(Record(new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))),
                path => Record(new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)),
                () => Task.CompletedTask, default);
        }

        RecordingStream Record(FileStream stream)
        {
            streams.Add(new(stream));
            return streams[^1];
        }

        // Nothing was written since the read, so there is no record left to read: only the append handle is opened.
        var validated = new FileInfo(fixture.Journal).Length;
        var notes = new List<AgentNotesEvent> { first, Note("unchanged") };
        await AppendAsync(notes[^1]);
        Assert.AreEqual(1, streams.Count, "An unchanged journal was opened for scanning.");
        Assert.IsTrue(streams[0].LowestRead >= validated - AgentJournalNotesReader.GuardBytes, "The scanned prefix was read again.");
        Assert.IsTrue(streams[0].BytesRead <= AgentJournalNotesReader.GuardBytes + 1, "A scanned record was read again.");

        // Each append leaves behind what it validated, so the next one reads only the records written since.
        foreach (var markdown in new[] { "second", "third" })
        {
            await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Update("before " + markdown)]);
            var length = new FileInfo(fixture.Journal).Length;
            notes.Add(Note(markdown));
            await AppendAsync(notes[^1]);
            Assert.AreEqual(2, streams.Count);
            foreach (var stream in streams)
            {
                Assert.IsTrue(stream.LowestRead >= validated - AgentJournalNotesReader.GuardBytes, "The validated prefix was read again.");
                Assert.IsTrue(stream.BytesRead <= length - validated + (2 * AgentJournalNotesReader.GuardBytes) + 1, "More than the unvalidated records was read.");
            }

            validated = length;
        }

        Assert.AreEqual(notes[^1], await fixture.Store.ReadLatestNotesAsync("session"));
        CollectionAssert.AreEqual(notes, (await fixture.Store.ReadEventsAsync("session")).OfType<AgentNotesEvent>().ToList());
    }

    [TestMethod]
    public async Task NotesAppend_WithoutAScannedPrefix_DoesNotDelayHistoryPagesOrAppendsWhileScanning()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var first = Note("first");
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session",
            [.. Enumerable.Range(0, 200).Select(index => Update("before " + index)), first]);
        var unscanned = new FileInfo(fixture.Journal).Length;
        var scanning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingStream? appendStream = null;
        var second = Note("second");
        var append = fixture.JournalFile.AppendNotesLineAsync(fixture.Journal, second.ToJson(), Utf8, async (path, token) =>
        {
            scanning.SetResult();
            await resume.Task.WaitAsync(token);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }, path => appendStream = new(new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)), () => Task.CompletedTask, default);
        await scanning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var during = Update("during");
        try
        {
            var page = await fixture.Store.ReadTimelinePageAsync("session", null, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(first, page.Entries.Select(entry => entry.Event).OfType<AgentNotesEvent>().Single());
            await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [during]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(append.IsCompleted);
        }
        finally
        {
            resume.SetResult();
        }

        await append.WaitAsync(TimeSpan.FromSeconds(5));
        // Under the journal gate only the record written during the scan was left to validate.
        Assert.IsTrue(appendStream!.LowestRead >= unscanned - AgentJournalNotesReader.GuardBytes, "The scanned records were read again under the journal gate.");
        var events = await fixture.Store.ReadEventsAsync("session");
        CollectionAssert.AreEqual(new AgentEvent[] { first, during, second }, events.Skip(events.Count - 3).ToArray());
        Assert.AreEqual(second, await fixture.Store.ReadLatestNotesAsync("session"));
    }

    [TestMethod]
    public async Task NotesAppend_AfterAScannedPrefix_StillRefusesInvalidContentWithoutChangingBytes()
    {
        // Deliberately corrupt persisted data; the production event contract is non-nullable.
        var withoutMarkdown = Note(null!).ToJson();
        (string Tail, Type Error)[] cases =
        [
            ("{broken", typeof(JsonException)),
            ("{broken\n", typeof(JsonException)),
            ("{broken\n" + Update("hidden").ToJson() + "\n", typeof(JsonException)),
            (withoutMarkdown, typeof(InvalidDataException)),
            (withoutMarkdown + "\n", typeof(InvalidDataException)),
        ];
        foreach (var (tail, error) in cases)
        {
            using var temp = TestTempDirectory.Create();
            var fixture = await Fixture.CreateAsync(temp.Path);
            await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Note("retained"), Update("after")]);
            Assert.AreEqual("retained", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);
            await File.AppendAllTextAsync(fixture.Journal, tail, Utf8);
            var before = await File.ReadAllBytesAsync(fixture.Journal);

            // Twice: a refusal must not leave the refused records remembered as validated.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var notified = false;
                var thrown = await ThrownByAsync(() => fixture.Store.AppendNotesAsync(Note("wrong"), () => { notified = true; return Task.CompletedTask; }));
                Assert.AreEqual(error, thrown?.GetType(), tail);
                Assert.IsFalse(notified, tail);
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Journal), tail);
            }
        }
    }

    [TestMethod]
    public async Task NotesAppend_ToARewrittenJournal_IsValidatedFromTheStart()
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        await fixture.Store.AppendEventsAsync("fixture", "notes-provider", "session", [Note("old"), Update("after")]);
        Assert.AreEqual("old", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);
        var original = await File.ReadAllTextAsync(fixture.Journal, Utf8);

        // Longer, with every scanned record moved behind a malformed one.
        await File.WriteAllTextAsync(fixture.Journal, "{broken\n" + original, Utf8);
        var before = await File.ReadAllBytesAsync(fixture.Journal);
        Assert.IsInstanceOfType<JsonException>(await ThrownByAsync(() => fixture.Store.AppendNotesAsync(Note("wrong"), () => Task.CompletedTask)));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Journal));

        // Same length, later write time, with a record replaced by a malformed one of the same size.
        await File.WriteAllTextAsync(fixture.Journal, original, Utf8);
        await fixture.Store.AppendNotesAsync(Note("valid"), () => Task.CompletedTask);
        Assert.AreEqual("valid", (await fixture.Store.ReadLatestNotesAsync("session"))!.Markdown);
        var valid = await File.ReadAllTextAsync(fixture.Journal, Utf8);
        var record = Note("old").ToJson();
        Assert.IsTrue(valid.Contains(record, StringComparison.Ordinal));
        await File.WriteAllTextAsync(fixture.Journal, valid.Replace(record, new string('x', record.Length), StringComparison.Ordinal), Utf8);
        File.SetLastWriteTimeUtc(fixture.Journal, File.GetLastWriteTimeUtc(fixture.Journal).AddSeconds(2));
        before = await File.ReadAllBytesAsync(fixture.Journal);
        Assert.IsInstanceOfType<JsonException>(await ThrownByAsync(() => fixture.Store.AppendNotesAsync(Note("wrong"), () => Task.CompletedTask)));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Journal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NotesAppend_OnEveryPrefixAndItsContinuation_MatchesStrictTextValidation(bool byteOrderMark)
    {
        using var temp = TestTempDirectory.Create();
        var fixture = await Fixture.CreateAsync(temp.Path);
        var journal = fixture.Journal;
        var content = await MixedTerminatorJournalAsync(journal, byteOrderMark);
        var appended = Note("appended");

        for (var length = 0; length <= content.Length; length++)
        {
            // A new journal owner per prefix: nothing is remembered from the previous length.
            var journalFile = new AgentSessionJournalFile();
            var store = new FileSystemAgentSessionStore(fixture.Layout, journalFile);
            await AssertAppendAsync(content[..length], "Prefix " + length);
            await AssertAppendAsync(content, "Continuation of " + length);

            async Task AssertAppendAsync(byte[] existing, string message)
            {
                await File.WriteAllBytesAsync(journal, existing);
                var expected = StrictTextValidationError(existing);
                var committed = false;
                var thrown = await ThrownByAsync(() => journalFile.AppendNotesLineAsync(journal, appended.ToJson(), Utf8,
                    static (path, _) => Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)),
                    () => { committed = true; return Task.CompletedTask; }, default));
                Assert.AreEqual(expected, thrown?.GetType(), message);
                Assert.AreEqual(expected is null, committed, message);
                var after = await File.ReadAllBytesAsync(journal);
                CollectionAssert.AreEqual(existing, after[..existing.Length], message);
                if (expected is not null)
                {
                    Assert.AreEqual(existing.Length, after.Length, message);
                    return;
                }

                // The new record is separated from an unterminated final one, so it is the last complete record.
                Assert.AreEqual(appended, (await store.ReadEventsAsync("session"))[^1], message);
                Assert.AreEqual(appended, await store.ReadLatestNotesAsync("session"), message);
            }
        }
    }

    // What the append validation did before it resumed from a scanned prefix: every line through the text reader.
    private static Type? StrictTextValidationError(byte[] journal)
    {
        using var reader = new StreamReader(new MemoryStream(journal), Utf8, detectEncodingFromByteOrderMarks: true);
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)
                    && (JsonSerializer.Deserialize(line, AgentJsonSerializerContext.Default.AgentEvent) ?? throw new JsonException()) is AgentNotesEvent { Markdown: null })
                {
                    return typeof(InvalidDataException);
                }
            }
        }
        catch (JsonException)
        {
            return typeof(JsonException);
        }

        return null;
    }

    private static async Task<Exception?> ThrownByAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return exception;
        }
    }

    // The fixture header followed by records using every line terminator, blank lines and an unterminated final record.
    private static async Task<byte[]> MixedTerminatorJournalAsync(string journal, bool byteOrderMark)
    {
        var header = await File.ReadAllTextAsync(journal, Utf8);
        return (byteOrderMark ? Encoding.UTF8.GetPreamble() : [])
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
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
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
