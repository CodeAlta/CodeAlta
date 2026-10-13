using CodeAlta.Catalog.Speech;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SpeechSettingsTests
{
    [TestMethod]
    public void Defaults_AreOnWithTheDefaultModel()
    {
        Assert.AreEqual(new SpeechSettings(true, "parakeet-tdt-0.6b-v3"), SpeechSettings.Read(null));
        Assert.AreEqual(SpeechSettings.Default, SpeechSettings.Read(new CodeAltaSpeechSettingsDocument()));
        WithStore((store, _) => Assert.AreEqual(SpeechSettings.Default, store.LoadGlobalSpeechSettings()));
    }

    [TestMethod]
    [DataRow("parakeet-tdt-0.6b-v3")]
    [DataRow("whisper-small")]
    [DataRow("https://huggingface.co/owner/repository")]
    [DataRow("https://huggingface.co/owner/repository/tree/main/folder")]
    public void Model_IsAnIdOrAHuggingFaceAddress(string model)
    {
        Assert.IsNull(SpeechSettings.GetModelError(model));
        Assert.AreEqual(model.StartsWith("https", StringComparison.Ordinal), SpeechSettings.IsRepository(model));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Parakeet V3")]
    [DataRow("../models")]
    [DataRow("-model")]
    [DataRow("http://huggingface.co/owner/repository")]
    [DataRow("https://example.com/owner/repository")]
    [DataRow("https://huggingface.co/owner")]
    [DataRow("https://huggingface.co/owner/repository?download=true")]
    [DataRow("https://user@huggingface.co/owner/repository")]
    public void Model_RefusesWhatIsNeither(string model)
    {
        Assert.IsNotNull(SpeechSettings.GetModelError(model));
        Assert.Throws<InvalidDataException>(() => SpeechSettings.Read(new CodeAltaSpeechSettingsDocument { Model = model }));
    }

    [TestMethod]
    public void Config_RoundTripsChoices_PreservesUnknownKeys_OmitsDefaults()
    {
        WithStore((store, _) =>
        {
            File.WriteAllText(store.ConfigPath, "[speech]\nfuture_option = 42\n[custom]\nkeep = 'yes'\n");
            var custom = new SpeechSettings(false, "https://huggingface.co/owner/repository");
            store.SaveGlobalSpeechSettings(custom);
            Assert.AreEqual(custom, store.LoadGlobalSpeechSettings());
            var content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "enabled = false");
            StringAssert.Contains(content, "huggingface.co/owner/repository");

            store.SaveGlobalSpeechSettings(SpeechSettings.Default);
            content = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(content, "future_option = 42");
            StringAssert.Contains(content, "keep =");
            Assert.DoesNotContain("enabled", content);
            Assert.DoesNotContain("model", content);
            Assert.AreEqual(SpeechSettings.Default, store.LoadGlobalSpeechSettings());

            File.WriteAllText(store.ConfigPath, string.Empty);
            store.SaveGlobalSpeechSettings(custom);
            store.SaveGlobalSpeechSettings(SpeechSettings.Default);
            Assert.DoesNotContain("[speech", File.ReadAllText(store.ConfigPath));
        });
    }

    [TestMethod]
    public void SendMode_IsManualUnlessChosen_AndAnUnknownOneIsReported()
    {
        Assert.AreEqual(SpeechSettings.ManualSend, SpeechSettings.Default.Send);
        Assert.AreEqual(SpeechSettings.ManualSend, new SpeechSettings(true, "whisper-small").Send);
        WithStore((store, _) =>
        {
            var chosen = new SpeechSettings(true, SpeechSettings.DefaultModel, SpeechSettings.SendOnStop);
            store.SaveGlobalSpeechSettings(chosen);
            StringAssert.Contains(File.ReadAllText(store.ConfigPath), "send = \"on_stop\"");
            Assert.DoesNotContain("model", File.ReadAllText(store.ConfigPath));
            Assert.AreEqual(chosen, store.LoadGlobalSpeechSettings());
            store.SaveGlobalSpeechSettings(SpeechSettings.Default);
            Assert.DoesNotContain("[speech", File.ReadAllText(store.ConfigPath));

            Assert.Throws<InvalidDataException>(() => store.SaveGlobalSpeechSettings(chosen with { Send = "after_silence" }));
            File.WriteAllText(store.ConfigPath, "[speech]\nsend = 'always'\n");
            Assert.Throws<InvalidDataException>(() => store.LoadGlobalSpeechSettings());
        });
    }

    [TestMethod]
    public void InvalidModel_IsReportedAndNeverSaved()
    {
        WithStore((store, _) =>
        {
            Assert.Throws<InvalidDataException>(() => store.SaveGlobalSpeechSettings(new SpeechSettings(true, "Not A Model")));
            Assert.IsFalse(File.Exists(store.ConfigPath));
            File.WriteAllText(store.ConfigPath, "[speech]\nmodel = 'Not A Model'\n");
            Assert.Throws<InvalidDataException>(() => store.LoadGlobalSpeechSettings());
        });
    }

    [TestMethod]
    public void SpeechRoot_IsInTheSharedCache()
    {
        var options = new CatalogOptions { GlobalRoot = Path.Combine(Path.GetTempPath(), "alta-root") };
        Assert.AreEqual(Path.Combine(options.GlobalRoot, "cache", "speech"), options.SpeechRoot);
    }

    private static void WithStore(Action<CodeAltaConfigStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-speech-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }), root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
