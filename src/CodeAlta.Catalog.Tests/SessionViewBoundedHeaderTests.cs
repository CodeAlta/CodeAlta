using System.Text;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionViewBoundedHeaderTests
{
    [TestMethod]
    public async Task ExactPersistedHeaderAndMissingFileAreDistinguished()
    {
        using var f = new Fixture();
        var missing = await f.Store.ReadBoundedHeaderAsync("session-1", f.At);
        Assert.AreEqual(BoundedSessionHeaderStatus.Missing, missing.Status);
        var path = f.Write("session-1", f.At, "ProjectSession", "project-1", f.Workspace);
        var result = await f.Store.ReadBoundedHeaderAsync("session-1", f.At);
        Assert.AreEqual(BoundedSessionHeaderStatus.Found, result.Status);
        Assert.AreEqual("session-1", result.Header!.SessionId);
        Assert.AreEqual("project-1", result.Header.ProjectRef);
        Assert.IsTrue(result.BytesRead <= SessionViewJournalStore.MaximumBoundedHeaderBytes + 1);
        await File.AppendAllTextAsync(path, new string('x', 512 * 1024));
        Assert.AreEqual(BoundedSessionHeaderStatus.Found, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
    }

    [TestMethod]
    public async Task OversizedOrCorruptFirstLineAndUnsafeIdNeverCertifyMetadata()
    {
        using var f = new Fixture();
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("../session-1", f.At)).Status);
        var path = f.Write("session-1", f.At, "GlobalSession", null, f.Workspace);
        await File.WriteAllTextAsync(path, new string('x', SessionViewJournalStore.MaximumBoundedHeaderBytes + 128) + "\n");
        var tooLarge = await f.Store.ReadBoundedHeaderAsync("session-1", f.At);
        Assert.AreEqual(BoundedSessionHeaderStatus.Incomplete, tooLarge.Status);
        Assert.IsTrue(tooLarge.BytesRead <= SessionViewJournalStore.MaximumBoundedHeaderBytes + 1);
        await File.WriteAllBytesAsync(path, [0xff, 0xff, 0x0a]);
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        f.Write("session-1", f.At, "GlobalSession", null, f.Workspace);
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace("\"session_id\":", "\"session_id\":\"wrong\",\"session_id\":", StringComparison.Ordinal));
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        await File.WriteAllTextAsync(path, original.Replace("\"kind\":\"GlobalSession\",", "", StringComparison.Ordinal));
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        await File.WriteAllTextAsync(path, original.Replace("\"$type\":\"raw\"", "\"$type\":123", StringComparison.Ordinal));
        Assert.AreEqual(BoundedSessionHeaderStatus.Invalid, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
    }

    [TestMethod]
    public async Task LockedAndObservedLinkedJournalFailClosedAndCancellationThrows()
    {
        using var f = new Fixture();
        var path = f.Write("session-1", f.At, "GlobalSession", null, f.Workspace);
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Store.ReadBoundedHeaderAsync("session-1", f.At, new CancellationToken(true)));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(BoundedSessionHeaderStatus.ReadError, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        File.Delete(path);
        try
        {
            File.CreateSymbolicLink(path, Path.Combine(f.Root, "not-present"));
            Assert.AreEqual(BoundedSessionHeaderStatus.ReadError, (await f.Store.ReadBoundedHeaderAsync("session-1", f.At)).Status);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
    }

    [TestMethod]
    public void HeaderIsReadByOneBoundedRequest_NotByteByByte()
    {
        var at = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        var line = Fixture.HeaderLine("session-1", at, "ProjectSession", "project-1", "/workspace");
        var journal = Encoding.UTF8.GetBytes(line + "\n" + new string('x', 512 * 1024) + "\n");
        var stream = new ChunkStream(journal, int.MaxValue);
        var bytes = 0;

        var result = SessionViewJournalStore.ReadBoundedHeader(stream, "session-1", at, ref bytes, CancellationToken.None);

        Assert.AreEqual(BoundedSessionHeaderStatus.Found, result.Status);
        Assert.AreEqual("project-1", result.Header!.ProjectRef);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(line) + 1, result.BytesRead);
        Assert.AreEqual(result.BytesRead, bytes);
        Console.WriteLine($"Header of {result.BytesRead} bytes: {stream.Reads} read request(s), {stream.Returned} bytes read (one per byte before).");
        Assert.AreEqual(1, stream.Reads);
        Assert.IsTrue(stream.LargestRequest <= SessionViewJournalStore.BoundedHeaderReadBytes);
    }

    [TestMethod]
    public void ChunkedReadKeepsTheByteByByteOutcomes_AtEveryBoundary()
    {
        var at = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        var header = Fixture.HeaderLine("session-1", at, "GlobalSession", null, "/workspace");
        var size = Encoding.UTF8.GetByteCount(header);
        const int budget = SessionViewJournalStore.MaximumBoundedHeaderBytes;
        var chunk = SessionViewJournalStore.BoundedHeaderReadBytes;
        var lengths = new[] { size, chunk - 2, chunk - 1, chunk, chunk + 1, 2 * chunk - 1, 2 * chunk, budget - 1, budget, budget + 1, budget + 2 * chunk };
        foreach (var length in lengths)
        {
            foreach (var crlf in new[] { false, true })
            {
                var padding = length - size - (crlf ? 1 : 0);
                if (padding < 0) continue;
                foreach (var limit in new[] { 1, 7, chunk, int.MaxValue })
                {
                    // A header padded with JSON whitespace to its length, then a record that is not even UTF-8.
                    var padded = header + new string(' ', padding) + (crlf ? "\r" : "");
                    var journal = Encoding.UTF8.GetBytes(padded + "\n").Concat(new byte[] { 0xff, 0xfe, (byte)'\n' }).ToArray();
                    var (status, consumed) = length < budget ? (BoundedSessionHeaderStatus.Found, length + 1) : (BoundedSessionHeaderStatus.Incomplete, budget + 1);
                    AssertOutcome(journal, limit, status, consumed, $"line of {length} bytes, crlf {crlf}, reads of at most {limit}");
                }
            }
        }
        // No newline: the end of the file before the budget is an invalid header, the budget reached is an incomplete one.
        foreach (var length in new[] { 0, 1, chunk - 1, chunk, chunk + 1, budget, budget + 1, budget + 5 })
        foreach (var limit in new[] { 1, chunk, int.MaxValue })
        {
            var journal = Encoding.UTF8.GetBytes(new string('{', length));
            var (status, consumed) = length <= budget ? (BoundedSessionHeaderStatus.Invalid, length) : (BoundedSessionHeaderStatus.Incomplete, budget + 1);
            AssertOutcome(journal, limit, status, consumed, $"{length} bytes without newline, reads of at most {limit}");
        }

        void AssertOutcome(byte[] journal, int limit, BoundedSessionHeaderStatus status, int consumed, string what)
        {
            var stream = new ChunkStream(journal, limit);
            var bytes = 0;
            var result = SessionViewJournalStore.ReadBoundedHeader(stream, "session-1", at, ref bytes, CancellationToken.None);
            Assert.AreEqual(status, result.Status, what);
            Assert.AreEqual(consumed, result.BytesRead, what);
            Assert.AreEqual(consumed, bytes, what);
            Assert.IsTrue(stream.Returned <= budget + 1, what);
            Assert.IsTrue(stream.LargestRequest <= chunk, what);
        }
    }

    // Serves a journal by reads of at most a given size, and counts the read requests.
    private sealed class ChunkStream(byte[] content, int limit) : Stream
    {
        private int _position;
        public int Reads { get; private set; }
        public int Returned { get; private set; }
        public int LargestRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            LargestRequest = Math.Max(LargestRequest, count);
            var length = Math.Min(Math.Min(count, limit), content.Length - _position);
            content.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset));
            _position += length;
            Returned += length;
            return length;
        }
        public override int ReadByte() => throw new AssertFailedException("The header is read by requests, not byte by byte.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "alta-header-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset At { get; } = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        public string Workspace => Path.Combine(Root, "workspace");
        public SessionViewJournalStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(new CatalogOptions { GlobalRoot = Root }); }
        public string Write(string id, DateTimeOffset at, string kind, string? projectId, string path)
        {
            var journal = new AgentRuntimePathLayout(Root).GetSessionFilePath(id, at);
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            File.WriteAllText(journal, HeaderLine(id, at, kind, projectId, path) + "\n", new UTF8Encoding(false));
            return journal;
        }
        public static string HeaderLine(string id, DateTimeOffset at, string kind, string? projectId, string path)
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(new { session_id = id, kind, project_ref = projectId,
                working_directory = path, backend_id = "provider", provider_key = "provider", created_at = at });
            return "{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":" + raw + "}";
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
