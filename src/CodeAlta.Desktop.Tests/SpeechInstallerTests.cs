using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Desktop.Speech;
using static CodeAlta.Desktop.Tests.SpeechTestSupport;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SpeechInstallerTests
{
    private const string Address = "https://huggingface.co/owner/sherpa-onnx-nemo-parakeet-test";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [TestMethod]
    public async Task Install_DownloadsTheEngineTheDetectorAndTheModel_ThenLeavesOnlyFinishedFolders()
    {
        using var fixture = new Fixture();
        var installer = fixture.Create();
        var validated = new List<string>();
        installer.Validate = (model, _) =>
        {
            validated.Add(model.Manifest.Id);
            return Task.FromResult<string?>(null);
        };
        var states = new List<SpeechInstallSnapshot>();
        using var subscription = installer.Subscribe(snapshot => { lock (states) states.Add(snapshot); });
        Assert.AreEqual(SpeechInstallSnapshot.Idle, states[0], "a subscriber gets the current state at once");
        Assert.IsFalse(installer.IsEngineInstalled);

        Assert.IsTrue(installer.TryStart(Address));
        var last = await fixture.EndAsync(installer);
        var id = SpeechModelSource.IdOf(Address);
        Assert.AreEqual(new SpeechInstallSnapshot("done", id, "owner/sherpa-onnx-nemo-parakeet-test", fixture.Total, fixture.Total, null), last);
        lock (states)
        {
            CollectionAssert.Contains(states.Select(static state => state.State).ToArray(), "installing");
            Assert.IsTrue(states.Where(static state => state.TotalBytes > 0).Select(static state => state.ReceivedBytes).SequenceEqual(states.Where(static state => state.TotalBytes > 0).Select(static state => state.ReceivedBytes).Order()), "progress never goes back");
        }

        Assert.IsTrue(installer.IsEngineInstalled);
        CollectionAssert.AreEqual(new[] { "dep.lib", "engine.lib" }, installer.EngineLibraries.ToArray());
        Assert.AreEqual("dependency", File.ReadAllText(Path.Combine(installer.EngineDirectory, "dep.lib")));
        Assert.IsFalse(File.Exists(Path.Combine(installer.EngineDirectory, "engine.nupkg")), "the package is not kept once its libraries are out");
        Assert.AreEqual("voice detector", File.ReadAllText(installer.VoiceDetectorPath));

        var model = installer.Find(id)!;
        Assert.AreEqual(model.Manifest.Id, installer.List().Single().Manifest.Id);
        Assert.AreEqual((Address, Commit, nameof(SpeechModelFamily.NemoTransducer)), (model.Manifest.Source, model.Manifest.Commit, model.Manifest.Family));
        CollectionAssert.AreEquivalent(new[] { "encoder", "decoder", "joiner", "tokens" }, model.Manifest.Files.Select(static file => file.Role).ToArray());
        Assert.AreEqual(fixture.ModelBytes, model.Bytes);
        CollectionAssert.AreEqual(new[] { id }, validated);
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(fixture.Root, "*.partial", SearchOption.AllDirectories).Any());
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Root, "*.part", SearchOption.AllDirectories).Any());
        CollectionAssert.DoesNotContain(fixture.Http.Requests.Select(static request => request.Url).ToArray(), fixture.Resolve("encoder.onnx"), "the quantized file is the one downloaded");

        // A second download of the same model has nothing to fetch.
        var before = fixture.Http.Requests.Count;
        Assert.IsTrue(installer.TryStart(Address));
        Assert.AreEqual("done", (await fixture.EndAsync(installer)).State);
        Assert.AreEqual(2, fixture.Http.Requests.Count - before, "only the listing of the repository is read again");
        await installer.CloseAsync();
        Assert.IsFalse(installer.TryStart(Address), "a closed installer starts nothing");
    }

    [TestMethod]
    public async Task Install_GoesOnFromWhereAnInterruptedDownloadStopped()
    {
        using var fixture = new Fixture();
        var installer = fixture.Create();
        fixture.Http.FailAfter[fixture.Resolve("encoder.int8.onnx")] = 3000;
        Assert.IsTrue(installer.TryStart(Address));
        Assert.AreEqual(("failed", "network"), ((await fixture.EndAsync(installer)).State, installer.Current.Error));
        var id = SpeechModelSource.IdOf(Address);
        var part = Path.Combine(fixture.Root, "models", id + ".partial", "encoder.int8.onnx.part");
        Assert.AreEqual(3000, new FileInfo(part).Length, "what arrived stays");
        Assert.IsNull(installer.Find(id));
        Assert.IsTrue(installer.IsEngineInstalled, "what was finished is not downloaded again");

        fixture.Http.FailAfter.Clear();
        fixture.Http.Requests.Clear();
        Assert.IsTrue(installer.TryStart(Address));
        Assert.AreEqual("done", (await fixture.EndAsync(installer)).State);
        Assert.AreEqual(3000, fixture.Http.Requests.Single(request => request.Url == fixture.Resolve("encoder.int8.onnx")).RangeFrom);
        Assert.AreEqual(1, fixture.Http.Requests.Count(static request => request.Url.Contains("/resolve/", StringComparison.Ordinal)), "the files that were complete are not asked for again");
        Assert.IsNotNull(installer.Find(id));
        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Install_RefusesAFileThatIsNotTheOnePinned()
    {
        using var fixture = new Fixture();
        var installer = fixture.Create();
        var bytes = fixture.Http.Content[fixture.Resolve("decoder.int8.onnx")];
        bytes[0] ^= 0xFF;
        Assert.IsTrue(installer.TryStart(Address));
        Assert.AreEqual(("failed", "checksum"), ((await fixture.EndAsync(installer)).State, installer.Current.Error));
        var id = SpeechModelSource.IdOf(Address);
        Assert.IsNull(installer.Find(id));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Root, "decoder*", SearchOption.AllDirectories).Any(), "a file that is not the expected one is removed, not kept for a resume");

        await installer.CloseAsync();

        // The token list is checked too, by the hash git names it by.
        using var other = new Fixture();
        var second = other.Create();
        other.Http.Content[other.Resolve("tokens.txt")][0] ^= 0xFF;
        Assert.IsTrue(second.TryStart(Address));
        Assert.AreEqual(("failed", "checksum"), ((await other.EndAsync(second)).State, second.Current.Error));
        await second.CloseAsync();
    }

    [TestMethod]
    public async Task Install_IsOneAtATime_CanBeCanceled_AndYieldsToAnotherInstance()
    {
        using var fixture = new Fixture();
        var installer = fixture.Create();
        var id = SpeechModelSource.IdOf(Address);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Hold[fixture.Resolve("encoder.int8.onnx")] = held.Task;
        Assert.IsTrue(installer.TryStart(Address));
        await Until(() => fixture.Http.Requests.Any(request => request.Url == fixture.Resolve("encoder.int8.onnx")), "the download of the large file");
        Assert.IsFalse(installer.TryStart(Address), "one download at a time");
        Assert.IsFalse(installer.Delete(id), "a model being downloaded is not removed");
        installer.Cancel();
        Assert.AreEqual("canceled", (await fixture.EndAsync(installer)).State);
        Assert.IsNull(installer.Find(id));
        held.SetResult();
        fixture.Http.Hold.Clear();

        // The developer and the normal instance share the cache: the one that holds the lock downloads.
        var partial = Path.Combine(fixture.Root, "models", id + ".partial");
        using (new FileStream(Path.Combine(partial, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsTrue(installer.TryStart(Address));
            Assert.AreEqual(("failed", "busy_elsewhere"), ((await fixture.EndAsync(installer)).State, installer.Current.Error));
        }

        Assert.IsTrue(installer.TryStart(Address));
        Assert.AreEqual("done", (await fixture.EndAsync(installer)).State);

        Assert.IsTrue(installer.Delete(id));
        Assert.IsNull(installer.Find(id));
        Assert.AreEqual(0, installer.List().Count);
        Assert.IsFalse(installer.Delete("../models"), "only a model of the cache is removed");
        Assert.IsTrue(installer.IsEngineInstalled, "the engine stays for the other models");
        await installer.CloseAsync();
    }

    [TestMethod]
    public async Task Install_SaysWhyAModelCannotBeInstalled_BeforeDownloadingIt()
    {
        using var fixture = new Fixture();
        var installer = fixture.Create();
        installer.Validate = static (_, _) => Task.FromResult<string?>("load_failed");
        Assert.IsTrue(installer.TryStart(Address));
        Assert.AreEqual(("failed", "load_failed"), ((await fixture.EndAsync(installer)).State, installer.Current.Error));
        Assert.IsNotNull(installer.Find(SpeechModelSource.IdOf(Address)), "the files stay: the page lists the model and can remove it");

        // Files the engine has no way to load here: nothing of the model is downloaded.
        fixture.Http.Requests.Clear();
        fixture.Http.Content[Fixture.Tree("owner/other")] = Encoding.UTF8.GetBytes("""[{"type":"file","path":"model.int8.onnx","size":10,"oid":"x","lfs":{"oid":"y"}},{"type":"file","path":"tokens.txt","size":3,"oid":"z"}]""");
        fixture.Http.Content["https://huggingface.co/api/models/owner/other"] = Encoding.UTF8.GetBytes($$"""{"sha":"{{Commit}}"}""");
        Assert.IsTrue(installer.TryStart("https://huggingface.co/owner/other"));
        Assert.AreEqual(("failed", "unsupported_model"), ((await fixture.EndAsync(installer)).State, installer.Current.Error));
        Assert.IsFalse(fixture.Http.Requests.Any(static request => request.Url.Contains("/resolve/", StringComparison.Ordinal)));

        Assert.IsTrue(installer.TryStart("https://huggingface.co/owner/missing"));
        Assert.AreEqual(("failed", "not_found"), ((await fixture.EndAsync(installer)).State, installer.Current.Error));
        await installer.CloseAsync();

        var nowhere = new SpeechInstaller(fixture.Root, fixture.Http, null, fixture.Detector);
        Assert.IsFalse(nowhere.IsSupported);
        Assert.IsTrue(nowhere.TryStart(Address));
        Assert.AreEqual(("failed", "unsupported_platform"), ((await fixture.EndAsync(nowhere)).State, nowhere.Current.Error));
        await nowhere.CloseAsync();
    }

    [TestMethod]
    public async Task Verify_UsesTheHashTheSourceGives()
    {
        var path = Path.Combine(Path.GetTempPath(), "codealta-speech-verify-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "hello\n");
        try
        {
            // `git hash-object` of a file holding "hello\n".
            Assert.IsTrue(await SpeechInstaller.VerifyAsync(path, new("f", "", 6, null, "ce013625030ba8dba906f756967f9e9ca394464a"), default));
            Assert.IsFalse(await SpeechInstaller.VerifyAsync(path, new("f", "", 6, null, "0000000000000000000000000000000000000000"), default));
            Assert.IsTrue(await SpeechInstaller.VerifyAsync(path, new("f", "", 6, "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03", null), default));
            Assert.IsFalse(await SpeechInstaller.VerifyAsync(path, new("f", "", 6, new string('0', 64), null), default));
            Assert.IsTrue(await SpeechInstaller.VerifyAsync(path, new("f", "", 6, null, null), default), "a source that gives no hash is checked by size only");
        }
        finally { File.Delete(path); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SpeechEngineDefinition _engine;

        public Fixture()
        {
            Root = NewRoot();
            using var package = new MemoryStream();
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, "runtimes/test-rid/native/dep.lib", "dependency");
                Write(archive, "runtimes/test-rid/native/engine.lib", "engine");
                Write(archive, "runtimes/other-rid/native/engine.lib", "another platform");
            }

            var nupkg = package.ToArray();
            _engine = new("test-rid", nupkg.Length, Sha256(nupkg), ["dep.lib", "engine.lib"]);
            Http.Content[_engine.Address.AbsoluteUri] = nupkg;
            var detector = "voice detector"u8.ToArray();
            Detector = new("vad", "Detector", "tests", "", "MIT", "tests/vad", Commit, "", SpeechModelFamily.Transducer, [new("silero_vad.onnx", "", detector.Length, Sha256(detector), null)]);
            Http.Content[Detector.AddressOf(Detector.Files[0]).AbsoluteUri] = detector;

            var files = new (string Name, byte[] Content, bool Large)[]
            {
                ("encoder.onnx", new byte[9000], true),
                ("encoder.int8.onnx", Enumerable.Range(0, 6000).Select(static value => (byte)value).ToArray(), true),
                ("decoder.int8.onnx", Enumerable.Range(0, 700).Select(static value => (byte)(value * 3)).ToArray(), true),
                ("joiner.int8.onnx", Enumerable.Range(0, 300).Select(static value => (byte)(value * 7)).ToArray(), true),
                ("tokens.txt", "a 0\nb 1\n<blk> 2\n"u8.ToArray(), false),
            };
            var entries = new List<string> { """{"type":"directory","path":"test_wavs","size":0,"oid":"d"}""" };
            foreach (var (name, content, large) in files)
            {
                Http.Content[Resolve(name)] = content;
                var entry = "{\"type\":\"file\",\"path\":\"" + name + "\",\"size\":" + content.Length;
                entries.Add(large
                    ? entry + ",\"oid\":\"pointer\",\"lfs\":{\"oid\":\"" + Sha256(content) + "\",\"size\":" + content.Length + "}}"
                    : entry + ",\"oid\":\"" + GitSha1(content) + "\"}");
                if (name != "encoder.onnx") ModelBytes += content.Length;
            }

            Http.Content["https://huggingface.co/api/models/owner/sherpa-onnx-nemo-parakeet-test"] = Encoding.UTF8.GetBytes($$"""{"sha":"{{Commit}}","id":"owner/sherpa-onnx-nemo-parakeet-test"}""");
            Http.Content[Tree("owner/sherpa-onnx-nemo-parakeet-test")] = Encoding.UTF8.GetBytes("[" + string.Join(",", entries) + "]");
            Total = nupkg.Length + detector.Length + ModelBytes;
        }

        public string Root { get; }
        public Handler Http { get; } = new();
        public SpeechModelDefinition Detector { get; }
        public long ModelBytes { get; }
        public long Total { get; }

        public SpeechInstaller Create() => new(Root, Http, _engine, Detector);

        public string Resolve(string name) => $"https://huggingface.co/owner/sherpa-onnx-nemo-parakeet-test/resolve/{Commit}/{name}";

        public static string Tree(string repository) => $"https://huggingface.co/api/models/{repository}/tree/{Commit}";

        public async Task<SpeechInstallSnapshot> EndAsync(SpeechInstaller installer)
        {
            await Until(() => !installer.Current.IsRunning, "the download to end");
            return installer.Current;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static void Write(ZipArchive archive, string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

        private static string GitSha1(byte[] content) => Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content]));
    }

    /// <summary>Hugging Face and nuget.org in memory: files by address, with ranges, and connections that break or wait.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Content { get; } = [];
        public Dictionary<string, int> FailAfter { get; } = [];
        public Dictionary<string, Task> Hold { get; } = [];
        public List<(string Url, long? RangeFrom)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var from = request.Headers.Range?.Ranges.Single().From;
            lock (Requests) Requests.Add((url, from));
            if (Hold.TryGetValue(url, out var hold)) await hold.WaitAsync(cancellationToken);
            if (!Content.TryGetValue(url, out var bytes)) return new(HttpStatusCode.NotFound);
            var body = bytes.AsSpan((int)(from ?? 0)).ToArray();
            Stream stream = new MemoryStream(body);
            if (FailAfter.TryGetValue(url, out var limit)) stream = new BrokenStream(body, limit);
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = body.Length;
            if (from is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, bytes.Length - 1, bytes.Length);
            return response;
        }
    }

    private sealed class BrokenStream(byte[] content, int limit) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit) throw new IOException("The connection was closed.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - Position)], cancellationToken);
        }
    }
}
