using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.Desktop.Speech;

namespace CodeAlta.Desktop.Tests;

/// <summary>The download of a model of search by meaning and of the ONNX Runtime it shares with speech.</summary>
[TestClass]
public sealed class EmbeddingInstallerTests
{
    [TestMethod]
    public async Task Install_GetsTheRuntimeAndTheModel_ThenDeleteRemovesTheModelOnly()
    {
        using var fixture = new Fixture();
        var installer = fixture.Create();
        var states = new List<EmbeddingInstallSnapshot>();
        installer.Changed += snapshot => { lock (states) states.Add(snapshot); };
        Assert.IsFalse(installer.IsInstalled(fixture.Model));
        Assert.IsNull(installer.RuntimeLibrary());

        await installer.InstallAsync(fixture.Model);
        Assert.AreEqual("done", installer.Current.State, installer.Current.Error);
        Assert.AreEqual(installer.Current.TotalBytes, installer.Current.ReceivedBytes);
        Assert.IsTrue(installer.IsInstalled(fixture.Model));
        Assert.AreEqual("graph", File.ReadAllText(Path.Combine(installer.DirectoryOf(fixture.Model), "model.onnx")));
        Assert.AreEqual("runtime", File.ReadAllText(installer.RuntimeLibrary()!));
        Assert.AreEqual(fixture.ModelBytes, installer.BytesOf(fixture.Model));
        lock (states) Assert.IsTrue(states.Any(static state => state.State == "downloading"));

        // Nothing to fetch the second time.
        var requests = fixture.Http.Requests.Count;
        await installer.InstallAsync(fixture.Model);
        Assert.AreEqual(requests, fixture.Http.Requests.Count);

        Assert.IsTrue(installer.Delete(fixture.Model));
        Assert.IsFalse(installer.IsInstalled(fixture.Model));
        Assert.IsNotNull(installer.RuntimeLibrary(), "the runtime stays: speech may use it");
    }

    [TestMethod]
    public async Task AFileWithAnotherHash_FailsTheDownload_AndAPlatformWithoutRuntimeIsSaidSo()
    {
        using var fixture = new Fixture();
        fixture.Http.Content[fixture.Model.Files[0].Address.AbsoluteUri] = "tampered"u8.ToArray();
        var installer = fixture.Create();
        await installer.InstallAsync(fixture.Model);
        Assert.AreEqual(("failed", "checksum"), (installer.Current.State, installer.Current.Error));
        Assert.IsFalse(installer.IsInstalled(fixture.Model));

        var unsupported = new EmbeddingInstaller(Path.Combine(fixture.Root, "search"), Path.Combine(fixture.Root, "runtime"), Path.Combine(fixture.Root, "speech"), null, fixture.Http);
        Assert.IsFalse(unsupported.Supported);
        await unsupported.InstallAsync(fixture.Model);
        Assert.AreEqual(("failed", "unsupported_platform"), (unsupported.Current.State, unsupported.Current.Error));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-embedding-" + Guid.NewGuid().ToString("N"))).FullName;
            byte[] graph = "graph"u8.ToArray(), tokenizer = "tokens"u8.ToArray();
            const string commit = "0123456789abcdef0123456789abcdef01234567";
            Model = new EmbeddingModelDefinition("test-model", "Test", "Tests", "", "MIT", 4, "model.onnx", "tokenizer.json", EmbeddingTokenizerKind.ByteLevelBpe,
                EmbeddingPooling.FirstToken, "{query}", "{query}", "{text}", "1",
            [
                new("model.onnx", "owner/model", commit, "onnx/model.onnx", graph.Length, Sha256(graph)),
                new("tokenizer.json", "owner/model", commit, "tokenizer.json", tokenizer.Length, Sha256(tokenizer)),
            ]);
            ModelBytes = graph.Length + tokenizer.Length;
            Http.Content[Model.Files[0].Address.AbsoluteUri] = graph;
            Http.Content[Model.Files[1].Address.AbsoluteUri] = tokenizer;
            using var package = new MemoryStream();
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("runtimes/test-rid/native/libonnxruntime.test").Open(), Encoding.UTF8);
                writer.Write("runtime");
            }

            var bytes = package.ToArray();
            Engine = new SpeechEngineDefinition("test-rid", bytes.Length, Sha256(bytes), ["libonnxruntime.test"]);
            Http.Content[Engine.Address.AbsoluteUri] = bytes;
        }

        public string Root { get; }
        public EmbeddingModelDefinition Model { get; }
        public long ModelBytes { get; }
        public SpeechEngineDefinition Engine { get; }
        public Handler Http { get; } = new();

        public EmbeddingInstaller Create()
            => new(Path.Combine(Root, "search"), Path.Combine(Root, "runtime"), Path.Combine(Root, "speech"), Engine, Http);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }

        private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>Hugging Face and nuget.org in memory.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Content { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (!Content.TryGetValue(url, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var body = bytes.AsSpan(from).ToArray();
            var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
            if (from > 0) response.Content.Headers.ContentRange = new(from, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
}
