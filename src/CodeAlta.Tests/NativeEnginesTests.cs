using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CodeAlta.NativeEngines;

namespace CodeAlta.Tests;

[TestClass]
public sealed class NativeEnginesTests
{
    [TestMethod]
    public async Task InstallFolder_DownloadsChecksAndNamesTheFolder()
    {
        using var fixture = new Fixture();
        var content = Enumerable.Range(0, 100_000).Select(static i => (byte)i).ToArray();
        fixture.Handler.Files["/model.bin"] = content;
        var file = new PinnedFile("model.bin", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        var progress = new Progress();
        var finished = false;

        await fixture.Downloads.InstallFolderAsync(fixture.Target, "marker.json", [(file, new Uri("https://example.test/model.bin"))],
            (partial, _) => { finished = File.Exists(Path.Combine(partial, "model.bin")); return Task.CompletedTask; }, "{}"u8.ToArray(), progress, default);

        Assert.IsTrue(finished, "finish runs in the partial folder once every file is there");
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Target, "marker.json")));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(fixture.Target, "model.bin")));
        Assert.IsFalse(Directory.Exists(fixture.Target + ".partial"));
        Assert.AreEqual(content.Length, progress.Bytes);
        Assert.AreEqual(1, progress.Checks);
    }

    [TestMethod]
    public async Task Download_ResumesAPartFileWithARange()
    {
        using var fixture = new Fixture();
        var content = Enumerable.Range(0, 50_000).Select(static i => (byte)(i * 7)).ToArray();
        fixture.Handler.Files["/model.bin"] = content;
        Directory.CreateDirectory(fixture.Target);
        File.WriteAllBytes(Path.Combine(fixture.Target, "model.bin.part"), content[..20_000]);
        var file = new PinnedFile("model.bin", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

        await fixture.Downloads.DownloadAsync(new Uri("https://example.test/model.bin"), file, fixture.Target, new Progress(), default);

        Assert.AreEqual(20_000, fixture.Handler.LastRangeFrom, "only what is missing is asked for");
        CollectionAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(fixture.Target, "model.bin")));
    }

    [TestMethod]
    public async Task Download_RefusesAFileWhoseHashDiffers_AndKeepsNoPart()
    {
        using var fixture = new Fixture();
        fixture.Handler.Files["/model.bin"] = [1, 2, 3, 4];
        var file = new PinnedFile("model.bin", 4, new string('0', 64));
        Directory.CreateDirectory(fixture.Target);

        var error = await Assert.ThrowsAsync<PinnedDownloadException>(() => fixture.Downloads.DownloadAsync(new Uri("https://example.test/model.bin"), file, fixture.Target, new Progress(), default));

        Assert.AreEqual("checksum", error.Code);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Target, "model.bin.part")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Target, "model.bin")));
    }

    [TestMethod]
    public async Task InstallFolder_IsBusyWhileAnotherInstanceHoldsTheLock()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Target + ".partial");
        using var held = new FileStream(Path.Combine(fixture.Target + ".partial", ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<PinnedDownloadException>(() => fixture.Downloads.InstallFolderAsync(fixture.Target, "marker.json", [], null, [], new Progress(), default));

        Assert.AreEqual("busy_elsewhere", error.Code);
    }

    [TestMethod]
    public async Task Read_RefusesADocumentLargerThanAllowed_AndAMissingOne()
    {
        using var fixture = new Fixture();
        fixture.Handler.Files["/listing.json"] = new byte[2048];

        Assert.HasCount(2048, await fixture.Downloads.ReadAsync("https://example.test/listing.json", 4096, default));
        Assert.AreEqual("too_large", (await Assert.ThrowsAsync<PinnedDownloadException>(() => fixture.Downloads.ReadAsync("https://example.test/listing.json", 1024, default))).Code);
        Assert.AreEqual("not_found", (await Assert.ThrowsAsync<PinnedDownloadException>(() => fixture.Downloads.ReadAsync("https://example.test/missing", 1024, default))).Code);
    }

    [TestMethod]
    public async Task Verify_ChecksSha256_ThenTheGitBlobSha1_AndTrustsAFileWithoutHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "hello\n");
            Assert.IsTrue(await PinnedDownloader.VerifyAsync(path, new("f", 6, "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03"), default));
            Assert.IsTrue(await PinnedDownloader.VerifyAsync(path, new("f", 6, null, "ce013625030ba8dba906f756967f9e9ca394464a"), default));
            Assert.IsFalse(await PinnedDownloader.VerifyAsync(path, new("f", 6, new string('0', 64)), default));
            Assert.IsTrue(await PinnedDownloader.VerifyAsync(path, new("f", 6, null), default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Frames_RoundTrip_AndRefuseWhatIsNotAFrame()
    {
        using var stream = new MemoryStream();
        WorkerFrames.Write(stream, 3, 42, "vectors"u8);
        WorkerFrames.Write(stream, 4, 0, []);
        stream.Position = 0;

        var first = WorkerFrames.Read(stream)!.Value;
        Assert.AreEqual((byte)3, first.Kind);
        Assert.AreEqual(42, first.Correlation);
        Assert.AreEqual("vectors", first.Text);
        Assert.AreEqual((byte)4, WorkerFrames.Read(stream)!.Value.Kind);
        Assert.IsNull(WorkerFrames.Read(stream));
        Assert.Throws<InvalidDataException>(() => WorkerFrames.Read(new MemoryStream("a log line\n"u8.ToArray())));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerFrames.Write(new MemoryStream(), 1, 1, new byte[WorkerFrames.MaximumContent + 1]));
    }

    private sealed class Progress : IPinnedDownloadProgress
    {
        public long Bytes { get; private set; }
        public int Checks { get; private set; }
        public void Received(long bytes) => Bytes += bytes;
        public void Checking(PinnedFile file) => Checks++;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codealta-pinned-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Downloads = new(Handler, new ProductInfoHeaderValue("CodeAlta", "test"));
        }

        public FileHandler Handler { get; } = new();
        public PinnedDownloader Downloads { get; }
        public string Target => Path.Combine(_root, "target");

        public void Dispose()
        {
            Downloads.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FileHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Files.TryGetValue(request.RequestUri!.AbsolutePath, out var content)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var from = request.Headers.Range?.Ranges.First().From;
            LastRangeFrom = from;
            var body = from is { } start ? content[(int)start..] : content;
            return Task.FromResult(new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) });
        }
    }
}
