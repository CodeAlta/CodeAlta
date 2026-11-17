using CodeAlta.Desktop.Rpc;
using CodeAlta.NativeEngines;
using CodeAlta.Plugin.Pseudonymizer;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PseudonymizerModelsTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public void EachLanguageNamesItsModel_AndIdentifiersOnlyNeedsNone()
    {
        using var folder = new TemporaryFolder();
        using var models = new PseudonymizerModelStore(new Host(folder.Path));
        var service = new PseudonymizerService(models, Epoch);

        var listed = service.Models(new(Epoch));
        Assert.AreEqual("ok", listed.Status);
        CollectionAssert.AreEqual(new[] { "french", "english", "multilingual", "identifiers" }, listed.Languages.Select(static language => language.Id).ToArray());
        Assert.AreEqual("CamemBERT-NER", listed.Languages[0].Model);
        Assert.AreEqual("BERT-base-NER", listed.Languages[1].Model, "An English project has a model of its own, not CamemBERT.");
        Assert.IsFalse(listed.Languages[0].Installed, "Nothing is downloaded before a project asks for it.");
        Assert.IsTrue(listed.Languages[3].Installed, "Identifiers only needs no download.");
        Assert.IsNull(listed.Languages[3].Model);
        Assert.AreEqual("idle", listed.Download!.State);

        Assert.IsFalse(models.Install(PseudonymizerLanguages.Identifiers), "Identifiers only downloads nothing.");
        Assert.AreEqual("stale_epoch", service.Install(new(Guid.NewGuid().ToString("D"), "french")).Status);
        Assert.AreEqual("invalid", service.Install(new(Epoch, "klingon")).Status);
        Assert.AreEqual("busy", service.Delete(new(Epoch, "french")).Status, "A model that is not there is not deleted.");
    }

    [TestMethod]
    public void ADownloadedModel_IsReady_AndItsDeletionFreesTheDisk()
    {
        using var folder = new TemporaryFolder();
        var host = new Host(folder.Path);
        using var models = new PseudonymizerModelStore(host);
        var english = Directory.CreateDirectory(Path.Combine(host.ModelsRoot, "bert-en-8e892123e8b7")).FullName;
        // A whole download: each file at its size, and the marker the download writes last.
        foreach (var (name, size) in new[] { ("model_quantized.onnx", 108952255L), ("vocab.txt", 213450L), ("config.json", 999L) })
        {
            using var file = File.Create(Path.Combine(english, name));
            file.SetLength(size);
        }

        File.WriteAllText(Path.Combine(english, "model.json"), "{}");
        Assert.IsTrue(models.IsReady(PseudonymizerLanguages.English));
        Assert.IsFalse(models.IsReady(PseudonymizerLanguages.French));
        Assert.IsFalse(models.Install(PseudonymizerLanguages.English), "A model that is there is not downloaded again.");

        var changed = 0;
        models.Changed += (_, _) => changed++;
        var service = new PseudonymizerService(models, Epoch);
        var deleted = service.Delete(new(Epoch, "english"));
        Assert.AreEqual("ok", deleted.Status);
        Assert.IsFalse(deleted.Languages.Single(static language => language.Id == "english").Installed);
        Assert.IsFalse(Directory.Exists(english));
        Assert.AreEqual(1, changed, "The plugin is told, and the status lines read again.");
    }

    [TestMethod]
    public void TheDescriptionOfAModelOfTheCatalog_IsACopy_AndTheCatalogIsNotChangedThroughIt()
    {
        var preset = NerModelCatalog.Find(NerModelCatalog.EnglishId)!;
        var description = preset.Description;
        description.Id = "changed";
        description.Model = "changed.onnx";
        description.Tokenizer.File = "changed.txt";

        Assert.AreEqual(NerModelCatalog.EnglishId, preset.Id);
        Assert.AreEqual("bert-en-8e892123e8b7", preset.FolderName);
        Assert.AreEqual("model_quantized.onnx", preset.Description.Model);
        Assert.AreEqual("vocab.txt", preset.Description.Tokenizer.File);
    }

    [TestMethod]
    public async Task ADownload_IsSaidAsSoonAsItIsAsked_AndEndsOnItsOutcome()
    {
        using var folder = new TemporaryFolder();
        var host = new Host(folder.Path) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var models = new PseudonymizerModelStore(host);
        var service = new PseudonymizerService(models, Epoch);

        // The window reads the models right after it asked for the download: both answers say it runs.
        var asked = service.Install(new(Epoch, "french"));
        Assert.AreEqual("downloading", asked.Download!.State);
        Assert.AreEqual("french", asked.Download.Language);
        Assert.AreEqual("downloading", service.Models(new(Epoch)).Download!.State);
        Assert.IsTrue(models.Install(PseudonymizerLanguages.English), "Another model waits its turn.");
        Assert.AreEqual("french", models.Download.Language);

        host.Gate.SetResult();
        await WaitForAsync(() => models.Download.State != PseudonymizerDownloadState.Downloading);
        Assert.AreEqual(PseudonymizerDownloadState.Failed, models.Download.State, "The host of this test downloads nothing.");
        Assert.AreEqual("english", models.Download.Language, "The model that waited was tried after the first.");
    }

    [TestMethod]
    public async Task ADamagedModel_IsRemoved_ToBeDownloadedAgain()
    {
        using var folder = new TemporaryFolder();
        var host = new Host(folder.Path);
        using var models = new PseudonymizerModelStore(host);
        var english = Directory.CreateDirectory(Path.Combine(host.ModelsRoot, "bert-en-8e892123e8b7")).FullName;
        // What an interrupted copy or a disk error leaves: the marker of a whole download, and a model cut short.
        foreach (var (name, size) in new[] { ("model_quantized.onnx", 1024L), ("vocab.txt", 213450L), ("config.json", 999L) })
        {
            using var file = File.Create(Path.Combine(english, name));
            file.SetLength(size);
        }

        File.WriteAllText(Path.Combine(english, "model.json"), "{}");
        Assert.IsFalse(models.IsReady(PseudonymizerLanguages.English));

        Assert.IsTrue(models.Install(PseudonymizerLanguages.English));
        await WaitForAsync(() => models.Download.State != PseudonymizerDownloadState.Downloading);

        Assert.IsFalse(Directory.Exists(english), "The damaged folder is removed: the download does not take it for an installation.");
        Assert.AreNotEqual(PseudonymizerDownloadState.Done, models.Download.State, "Nothing says the model is installed while it is not.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The download did not end.");
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task TheLanguageOfAProject_ChoosesTheIdentifiersOfItsCountries()
    {
        const string text = "SSN 219-09-9999.\nNuméro de sécurité sociale 1 85 05 75 112 345 29.\nsophie@exemple.fr";
        async Task<string[]> Kinds(string language)
            => [.. (await new PiiAnalyzer(PseudonymizerLanguages.IdentifierRecognizers(language)).AnalyzeAsync(text, CancellationToken.None)).Select(static span => span.Entity).Distinct()];

        var english = await Kinds(PseudonymizerLanguages.English);
        CollectionAssert.IsSubsetOf(new[] { PiiEntities.Email, PiiEntities.UsSsn }, english);
        CollectionAssert.DoesNotContain(english, PiiEntities.Nir);
        var french = await Kinds(PseudonymizerLanguages.French);
        CollectionAssert.IsSubsetOf(new[] { PiiEntities.Email, PiiEntities.Nir }, french);
        CollectionAssert.DoesNotContain(french, PiiEntities.UsSsn);
        CollectionAssert.IsSubsetOf(new[] { PiiEntities.Email, PiiEntities.Nir, PiiEntities.UsSsn }, await Kinds(PseudonymizerLanguages.Multilingual));
        CollectionAssert.IsSubsetOf(new[] { PiiEntities.Email, PiiEntities.Nir, PiiEntities.UsSsn }, await Kinds(PseudonymizerLanguages.Identifiers));
    }

    private sealed class Host(string root) : IPseudonymizerModelHost
    {
        public string ModelsRoot { get; } = System.IO.Path.Combine(root, "models");

        // What a download waits for before it fails, when a test holds it.
        public TaskCompletionSource? Gate { get; init; }

        public string? OnnxRuntimeLibrary() => null;

        public async Task InstallOnnxRuntimeAsync(PinnedDownloader downloads, IPinnedDownloadProgress progress, CancellationToken cancellationToken)
        {
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            throw new PlatformNotSupportedException("No download in a test.");
        }

        public IWorkerProcess StartWorker() => throw new InvalidOperationException("No worker in a test.");
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-pseudonymizer-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
