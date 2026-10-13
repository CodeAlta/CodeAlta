using CodeAlta.Catalog.Speech;
using CodeAlta.Desktop.Speech;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SpeechModelCatalogTests
{
    [TestMethod]
    public void Presets_AreCompleteAndPinned()
    {
        Assert.AreEqual(SpeechSettings.DefaultModel, SpeechModelCatalog.Presets[0].Id, "the default model is the first preset");
        Assert.AreEqual(SpeechModelCatalog.Presets.Count, SpeechModelCatalog.Presets.Select(static preset => preset.Id).Distinct().Count());
        foreach (var preset in SpeechModelCatalog.Presets)
        {
            Assert.IsNull(SpeechSettings.GetModelError(preset.Id), preset.Id);
            Assert.IsFalse(SpeechSettings.IsRepository(preset.Id));
            Assert.AreEqual(40, preset.Commit.Length, preset.Id);
            Assert.IsTrue(preset.Languages is "english" or "european" or "multilingual", preset.Id);
            Assert.IsTrue(preset.License.Length > 0 && preset.Author.Length > 0 && preset.Name.Length > 0, preset.Id);
            foreach (var file in preset.Files)
            {
                Assert.IsTrue(SpeechModelSource.IsSafeSegment(file.Name), file.Name);
                Assert.IsTrue(file.Size > 0);
                // A large file is checked by its SHA-256, the token list by the SHA-1 git names it by.
                Assert.IsTrue(file.Sha256 is { Length: 64 } ^ file.GitSha1 is { Length: 40 }, preset.Id + "/" + file.Name);
                StringAssert.StartsWith(preset.AddressOf(file).AbsoluteUri, $"https://huggingface.co/{preset.Repository}/resolve/{preset.Commit}/");
            }

            var roles = preset.Files.Select(static file => file.Role).Order().ToArray();
            CollectionAssert.AreEqual(preset.Family switch
            {
                SpeechModelFamily.NemoTransducer or SpeechModelFamily.Transducer => new[] { "decoder", "encoder", "joiner", "tokens" },
                SpeechModelFamily.MoonshineV2 => ["encoder", "mergedDecoder", "tokens"],
                SpeechModelFamily.MoonshineV1 => ["cachedDecoder", "encoder", "preprocessor", "tokens", "uncachedDecoder"],
                _ => ["decoder", "encoder", "tokens"],
            }, roles, preset.Id);
        }

        Assert.AreEqual(64, SpeechModelCatalog.VoiceDetector.Files.Single().Sha256!.Length);
    }

    [TestMethod]
    public void Engine_ExistsForEveryRuntimeOfTheDesktopPackage()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(DesktopArchitectureTests.SourceRoot, "CodeAlta", "CodeAlta.csproj"));
        var runtimes = project.Descendants("RuntimeIdentifiers").Single().Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CollectionAssert.AreEquivalent(runtimes, SpeechModelCatalog.EngineRuntimeIdentifiers.ToArray());
        foreach (var runtime in runtimes)
        {
            var engine = SpeechModelCatalog.FindEngine(runtime)!;
            Assert.AreEqual(64, engine.Sha256.Length);
            Assert.AreEqual(2, engine.Libraries.Count);
            StringAssert.Contains(engine.Libraries[^1], "sherpa-onnx-c-api", "the library the wrapper imports is loaded last, after what it needs");
            StringAssert.Contains(engine.Address.AbsoluteUri, $"/org.k2fsa.sherpa.onnx.runtime.{runtime}/{SpeechModelCatalog.EngineVersion}/");
        }

        // The wrapper the project references and the libraries downloaded are one version.
        var packages = System.Xml.Linq.XDocument.Load(Path.Combine(DesktopArchitectureTests.SourceRoot, "Directory.Packages.props"));
        Assert.AreEqual(SpeechModelCatalog.EngineVersion, packages.Descendants("PackageVersion").Single(static package => (string?)package.Attribute("Include") == "org.k2fsa.sherpa.onnx").Attribute("Version")!.Value);
        Assert.IsNull(SpeechModelCatalog.FindEngine("linux-musl-x64"));
        Assert.IsNull(SpeechModelCatalog.FindEngine(null));
    }

    [TestMethod]
    [DataRow("https://huggingface.co/owner/name", "owner/name", null, "")]
    [DataRow("https://huggingface.co/owner/name/", "owner/name", null, "")]
    [DataRow("https://huggingface.co/owner/name/tree/main", "owner/name", "main", "")]
    [DataRow("https://huggingface.co/owner/name/tree/v1.0/models/int8", "owner/name", "v1.0", "models/int8")]
    public void Address_NamesARepositoryARevisionAndAFolder(string address, string repository, string? revision, string folder)
    {
        Assert.IsTrue(SpeechModelSource.TryParse(address, out var parsed));
        Assert.AreEqual(new SpeechRepositoryAddress(repository, revision, folder), parsed);
        var id = SpeechModelSource.IdOf(address);
        Assert.IsNull(SpeechSettings.GetModelError(id), "the id of a custom model is a folder name of the cache");
        StringAssert.StartsWith(id, "custom-owner-name-");
        Assert.AreEqual(id, SpeechModelSource.ModelIdOf(address));
    }

    [TestMethod]
    [DataRow("parakeet-tdt-0.6b-v3")]
    [DataRow("https://huggingface.co/owner")]
    [DataRow("https://huggingface.co/owner/name/blob/main/file.onnx")]
    [DataRow("https://huggingface.co/owner/name/tree")]
    [DataRow("https://huggingface.co/owner/na%2Fme")]
    [DataRow("https://example.com/owner/name")]
    public void Address_RefusesWhatIsNotARepository(string address)
        => Assert.IsFalse(SpeechModelSource.TryParse(address, out _));

    [TestMethod]
    public void ModelId_IsThePresetOrTheCustomFolder_AndTwoAddressesDiffer()
    {
        Assert.AreEqual("whisper-small", SpeechModelSource.ModelIdOf("whisper-small"));
        Assert.IsNull(SpeechModelSource.ModelIdOf("not-a-preset"));
        Assert.IsNull(SpeechModelSource.ModelIdOf(null));
        Assert.AreNotEqual(SpeechModelSource.IdOf("https://huggingface.co/owner/name"), SpeechModelSource.IdOf("https://huggingface.co/owner/name/tree/main"));
    }

    [TestMethod]
    public void Classify_RecognizesEachFamily_AndPrefersQuantizedFiles()
    {
        var parakeet = Classify("owner/sherpa-onnx-nemo-parakeet", "encoder.onnx", "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt", "README.md")!;
        Assert.AreEqual(SpeechModelFamily.NemoTransducer, parakeet.Family);
        CollectionAssert.AreEqual(new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" }, parakeet.Files.Select(static file => file.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "encoder", "decoder", "joiner", "tokens" }, parakeet.Files.Select(static file => file.Role).ToArray());
        Assert.AreEqual("owner/sherpa-onnx-nemo-parakeet", parakeet.Name);
        Assert.AreEqual("owner", parakeet.Author);

        Assert.AreEqual(SpeechModelFamily.Transducer, Classify("owner/zipformer", "encoder-epoch-99.onnx", "decoder-epoch-99.onnx", "joiner-epoch-99.onnx", "tokens.txt")!.Family);
        var moonshine = Classify("owner/moonshine", "encoder_model.ort", "decoder_model_merged.ort", "tokens.txt", "LICENSE")!;
        Assert.AreEqual(SpeechModelFamily.MoonshineV2, moonshine.Family);
        CollectionAssert.AreEqual(new[] { "encoder", "mergedDecoder", "tokens" }, moonshine.Files.Select(static file => file.Role).ToArray());
        var first = Classify("owner/moonshine-v1", "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt")!;
        Assert.AreEqual(SpeechModelFamily.MoonshineV1, first.Family);
        CollectionAssert.AreEqual(new[] { "preprocessor", "encoder", "uncachedDecoder", "cachedDecoder", "tokens" }, first.Files.Select(static file => file.Role).ToArray());
        var whisper = Classify("owner/whisper", "small-encoder.onnx", "small-encoder.int8.onnx", "small-decoder.onnx", "small-decoder.int8.onnx", "small-tokens.txt")!;
        Assert.AreEqual(SpeechModelFamily.Whisper, whisper.Family);
        CollectionAssert.AreEqual(new[] { "small-encoder.int8.onnx", "small-decoder.int8.onnx", "small-tokens.txt" }, whisper.Files.Select(static file => file.Name).ToArray());
    }

    [TestMethod]
    public void Classify_RefusesWhatTheEngineDoesNotLoadHere()
    {
        Assert.IsNull(Classify("owner/sense-voice", "model.int8.onnx", "tokens.txt"), "one model file is several possible families");
        Assert.IsNull(Classify("owner/half", "encoder.onnx", "joiner.onnx", "tokens.txt"), "a part is missing");
        Assert.IsNull(Classify("owner/empty"));
        Assert.IsNull(Classify("owner/odd", "enc oder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"), "a name that is not a plain file name is not downloaded");
        var address = "https://huggingface.co/owner/huge";
        Assert.IsTrue(SpeechModelSource.TryParse(address, out var repository));
        var huge = new SpeechFile[] { new("encoder.onnx", "", SpeechModelSource.MaximumModelBytes, null, null), new("decoder.onnx", "", 1, null, null), new("joiner.onnx", "", 1, null, null), new("tokens.txt", "", 1, null, null) };
        Assert.IsNull(SpeechModelSource.Classify(address, repository, new string('a', 40), huge));
    }

    [TestMethod]
    [DataRow("encoder.int8.onnx", true)]
    [DataRow("small-tokens.txt", true)]
    [DataRow("..", false)]
    [DataRow(".hidden", false)]
    [DataRow("folder/file.onnx", false)]
    [DataRow("file.onnx.", false)]
    [DataRow("a b.onnx", false)]
    [DataRow("", false)]
    public void SafeSegment_IsAPlainFileName(string name, bool safe)
        => Assert.AreEqual(safe, SpeechModelSource.IsSafeSegment(name));

    private static SpeechModelDefinition? Classify(string repositoryName, params string[] names)
    {
        var address = "https://huggingface.co/" + repositoryName;
        Assert.IsTrue(SpeechModelSource.TryParse(address, out var repository));
        return SpeechModelSource.Classify(address, repository, new string('a', 40), [.. names.Select(static name => new SpeechFile(name, "", 10, null, null))]);
    }
}
