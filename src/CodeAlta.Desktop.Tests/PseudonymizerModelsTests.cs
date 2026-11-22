using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using CodeAlta.NativeEngines;
using CodeAlta.Plugin.Pseudonymizer;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Pseudonymization;
using CodeAlta.Pseudonymization.Ner;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class PseudonymizerModelsTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    // How the English model of these tests is run: what its download writes beside its files.
    private const string EnglishDescription = """
        {"id":"bert-en","model":"model_quantized.onnx","tokenizer":{"kind":"wordpiece","file":"vocab.txt"},"entities":{"PER":"PERSONNE"}}
        """;

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
    public void LoadingAPreviouslyDownloadedModel_DoesNotRewriteItsDescription()
    {
        using var folder = new TemporaryFolder();
        var host = new Host(folder.Path) { Runtime = "unused-in-this-test" };
        using var models = new NerModels(host);
        var descriptionPath = Path.Combine(InstallEnglish(host), NerModelDescription.FileName);

        Assert.IsInstanceOfType<TokenClassificationRecognizer>(models.Recognizer(NerModelCatalog.EnglishId, null));
        Assert.AreEqual(EnglishDescription, File.ReadAllText(descriptionPath), "The same installation may still be used by the previous app version.");
    }

    [TestMethod]
    public async Task AModelWithoutItsRuntime_GetsTheRuntimeAlone()
    {
        using var folder = new TemporaryFolder();
        var host = new Host(folder.Path) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), Downloaded = "onnxruntime" };
        using var models = new PseudonymizerModelStore(host);
        Assert.IsFalse(models.NeedsRuntime, "Without a model, nothing waits for the runtime: the first model comes with it.");
        // What a version of the application that runs on another ONNX Runtime finds: the model, and not its runtime.
        InstallEnglish(host);
        Assert.IsTrue(models.IsReady(PseudonymizerLanguages.English));
        Assert.IsTrue(models.NeedsRuntime);
        Assert.IsFalse(models.Install(PseudonymizerLanguages.English), "The model is not downloaded again.");

        var installing = models.InstallRuntimeAsync();
        Assert.IsFalse(installing.IsCompleted);
        host.Gate.SetResult();
        Assert.IsNull(await installing);
        Assert.IsFalse(models.NeedsRuntime);
        Assert.AreEqual(PseudonymizerDownloadState.Idle, models.Download.State, "It is not the download of a model.");

        // A download that fails says why.
        (host.Runtime, host.Downloaded) = (null, null);
        Assert.AreEqual("No download in a test.", await models.InstallRuntimeAsync());
        Assert.IsTrue(models.NeedsRuntime);
    }

    [TestMethod]
    public async Task ARequest_WaitsForTheRuntimeOfAModelThatIsThere_AndSendsNothingWhenItDoesNotCome()
    {
        using var folder = new TemporaryFolder();
        var host = new Host(folder.Path) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var models = new PseudonymizerModelStore(host);
        InstallEnglish(host);
        var activation = await new PluginRuntimeActivator(new PluginContributionRegistry()).ActivateAsync(
            new DiscoveredPluginType { Type = typeof(PseudonymizerPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(PseudonymizerPlugin)) }, null, null,
            new PluginActivationOptions
            {
                HostInfo = new PluginHostInfo { ApplicationName = "CodeAlta.Desktop.Tests", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = folder.Path, IsHeadless = true },
                Services = new EnglishProjects(),
                BuiltInFactory = () => new PseudonymizerPlugin(static _ => new PlainProtector(), names: null, models),
            });
        Assert.IsTrue(activation.Succeeded, string.Join(Environment.NewLine, activation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var active = activation.ActivePlugin!;
        var plugin = (PseudonymizerPlugin)active.Instance!;
        var session = new PluginProviderBoundaryContext { SessionId = "session", ProjectId = "project" };
        string? Status() => plugin.GetUiContributions().OfType<PluginStatusContribution>().Single()
            .GetStatus(new PluginStatusContext { Plugin = active.Descriptor, Services = active.RuntimeContext.Services, SessionId = "session", ProjectId = "project" })?.Text;

        // The plugin asks for the runtime as it starts, and the request that comes meanwhile asks too and waits.
        var preparing = plugin.GetProviderBoundary(session)!.PrepareRequestAsync(Request(), CancellationToken.None).AsTask();
        await WaitForAsync(() => Status() == "downloading the engine of the model");
        Assert.IsFalse(preparing.IsCompleted, "Nothing is sent before the model can run.");

        // A runtime that cannot be downloaded: the request says so, and nothing of it is sent.
        host.Gate.SetResult();
        var refused = await Assert.ThrowsExactlyAsync<AgentProviderBoundaryException>(() => preparing);
        StringAssert.Contains(refused.Message, "could not be downloaded (No download in a test.)");
        Assert.IsNull(Status());

        // Sent again, the request asks for the runtime again, and goes through the boundary of the model once it is there.
        host.Downloaded = "onnxruntime";
        var boundary = plugin.GetProviderBoundary(session)!;
        var sent = await boundary.PrepareRequestAsync(Request(), CancellationToken.None);
        StringAssert.Contains(sent.SessionInstructions, "Pseudonymization: on");
        var answer = boundary.CreateResponseStream(AgentContentKind.Assistant, "content");
        Assert.AreEqual("Done.", answer.Write("Done.") + answer.Flush(), "The answer is restored by the same boundary.");
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
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
        var deadline = DateTime.UtcNow + TestTime.Seconds(30);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The download did not end.");
            await Task.Delay(20);
        }
    }

    // Writes what a whole download of the English model leaves, with a tokenizer and labels that load; gives its folder.
    private static string InstallEnglish(Host host)
    {
        var preset = NerModelCatalog.Find(NerModelCatalog.EnglishId)!;
        var directory = Directory.CreateDirectory(Path.Combine(host.ModelsRoot, preset.FolderName)).FullName;
        foreach (var file in preset.Files)
        {
            var path = Path.Combine(directory, file.Name);
            if (file.Name == "model_quantized.onnx")
            {
                using var graph = File.Create(path);
                graph.SetLength(file.Size);
                continue;
            }

            var content = file.Name == "vocab.txt"
                ? "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nJohn\n"
                : """{"id2label":{"0":"O","1":"B-PER"}}""";
            File.WriteAllText(path, content.PadRight((int)file.Size));
        }

        File.WriteAllText(Path.Combine(directory, "model.json"), "{}");
        File.WriteAllText(Path.Combine(directory, NerModelDescription.FileName), EnglishDescription);
        return directory;
    }

    // A request that holds no text: nothing of it is given to the model, which these tests do not run.
    private static AgentTurnRequest Request()
    {
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "fixture",
            DisplayName = "Fixture",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://provider.invalid/v1"),
            IsDefault = true,
            Profile = new AgentProviderProfile { MaxTokensFieldName = "max_output_tokens" },
        };
        return new AgentTurnRequest
        {
            Provider = provider,
            ProviderId = new ModelProviderId("fixture"),
            SessionId = "session",
            RunId = new AgentRunId("run"),
            ModelId = "model",
            Conversation = [],
            Tools = [],
            State = new AgentSessionState { SessionId = "session", ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, UpdatedAt = DateTimeOffset.UtcNow },
        };
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

        // What a download of the runtime waits for before it ends, when a test holds it.
        public TaskCompletionSource? Gate { get; init; }

        public string? Runtime { get; set; }

        // The runtime a download installs; null for a download that fails.
        public string? Downloaded { get; set; }

        public string? OnnxRuntimeLibrary() => Runtime;

        public async Task InstallOnnxRuntimeAsync(CancellationToken cancellationToken)
        {
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            Runtime = Downloaded ?? throw new PlatformNotSupportedException("No download in a test.");
        }

        public IWorkerProcess StartWorker() => throw new InvalidOperationException("No worker in a test.");
    }

    // The services of a host whose projects all hold English documents, and nothing else.
    private sealed class EnglishProjects : IPluginServices, IPluginSettingsService
    {
        private readonly NoopPluginServices _inner = new(XenoAtom.Logging.LogManager.GetLogger("test"));

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State => _inner.State;

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta => _inner.Alta;

        public IPluginSettingsService Settings => this;

        public event EventHandler<PluginSettingChangedEventArgs>? Changed { add { } remove { } }

        public JsonElement? GetValue(string key, string? projectId = null)
            => key == PseudonymizerPlugin.LanguageKey && projectId is not null ? JsonSerializer.SerializeToElement(PseudonymizerLanguages.English) : null;
    }

    // Protects nothing: the pseudonyms of these tests are never written.
    private sealed class PlainProtector : CodeAlta.Catalog.Secrets.ISecretProtector
    {
        public string Kind => CodeAlta.Catalog.Secrets.SecretProtectionKinds.File;

        public byte[] Protect(ReadOnlySpan<byte> value) => value.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-pseudonymizer-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
