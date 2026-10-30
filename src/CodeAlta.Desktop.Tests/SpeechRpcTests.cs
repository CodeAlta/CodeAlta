using CodeAlta.Catalog;
using CodeAlta.Catalog.Speech;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.Speech;
using NeoAstra;
using static CodeAlta.Desktop.Tests.SpeechTestSupport;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SpeechRpcTests
{
    private const string Runtime = "win-x64";

    [TestMethod]
    public async Task UnownedAndStaleRequests_HaveNoAccess()
    {
        var unavailable = new SpeechService();
        Assert.AreEqual("unavailable", (await unavailable.ReadAsync(new(null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SaveSettingsAsync(new(null, true, "whisper-small"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.InstallAsync(new(null, null), default)).Status);
        Assert.AreEqual("unavailable", unavailable.CancelInstall(new(null)).Status);
        Assert.AreEqual("unavailable", (await unavailable.DeleteAsync(new(null, "whisper-small"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.StartAsync(new(null), default)).Status);
        Assert.AreEqual("unavailable", unavailable.Append(new(null, "id", 0, "AAAA")).Status);
        Assert.AreEqual("unavailable", (await unavailable.StopAsync(new(null, "id", false), default)).Status);
        Assert.IsFalse(unavailable.IsMicrophoneAllowed());
        Assert.AreEqual(0, await CountAsync(unavailable.WatchAsync(new(null), default)));
        Assert.AreEqual(0, await CountAsync(unavailable.ListenAsync(new(null, "id"), default)));
        await unavailable.CloseAsync();

        using var fixture = new Fixture();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadAsync(new("old"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SaveSettingsAsync(new("old", false, "whisper-small"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.StartAsync(new("old"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.DeleteAsync(new("old", "whisper-small"), default)).Status);
        Assert.AreEqual(0, await CountAsync(fixture.Service.WatchAsync(new("old"), default)));
        Assert.IsFalse(Directory.Exists(fixture.Root), "nothing is written by a request that is refused");
    }

    [TestMethod]
    public async Task Listing_ShowsThePresetsAndTheCache_AndSettingsRoundTrip()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Service.ReadAsync(new("host"), default);
        Assert.AreEqual(("ok", true, true, SpeechSettings.DefaultModel, SpeechSettings.DefaultModel), (initial.Status, initial.Supported, initial.Enabled, initial.Model, initial.ModelId));
        CollectionAssert.AreEqual(SpeechModelCatalog.Presets.Select(static preset => preset.Id).ToArray(), initial.Models.Select(static model => model.Id).ToArray());
        Assert.IsTrue(initial.Models.All(static model => model is { Preset: true, Installed: false, Bytes: > 0 }));
        Assert.AreEqual(Path.Combine(fixture.Root, "cache", "speech"), initial.RootPath);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "config.toml")), "reading does not write defaults");
        Assert.IsTrue(fixture.Service.IsMicrophoneAllowed());

        // A custom address is listed once chosen, before anything of it is downloaded.
        const string address = "https://huggingface.co/owner/custom-model";
        var custom = await fixture.Service.SaveSettingsAsync(new("host", true, " " + address + " "), default);
        Assert.AreEqual(("ok", address, SpeechModelSource.IdOf(address)), (custom.Status, custom.Model, custom.ModelId));
        Assert.AreEqual(new SpeechModelInfo(SpeechModelSource.IdOf(address), address, "owner/custom-model", "owner", "", "", 0, false, false), custom.Models[^1]);

        // Installed models: a preset shows what it takes on disk, a custom one is chosen by its address.
        InstallModel(fixture.SpeechRoot, Runtime, "whisper-small", "whisper-small");
        InstallModel(fixture.SpeechRoot, Runtime, SpeechModelSource.IdOf(address), address, "owner/custom-model");
        var listed = await fixture.Service.ReadAsync(new("host"), default);
        Assert.AreEqual(8L, listed.Models.Single(static model => model.Id == "whisper-small").Bytes);
        Assert.IsTrue(listed.Models.Single(static model => model.Id == "whisper-small").Installed);
        Assert.AreEqual((address, true, false), (listed.Models[^1].Value, listed.Models[^1].Installed, listed.Models[^1].Preset));
        Assert.AreEqual(SpeechModelCatalog.Presets.Count + 1, listed.Models.Count);

        var off = await fixture.Service.SaveSettingsAsync(new("host", false, "whisper-small"), default);
        Assert.AreEqual(("ok", false, "whisper-small"), (off.Status, off.Enabled, off.Model));
        Assert.IsFalse(fixture.Service.IsMicrophoneAllowed(), "turned off, the page gets no microphone");
        Assert.AreEqual(new SpeechSettings(false, "whisper-small"), new CodeAltaConfigStore(fixture.Options).LoadGlobalSpeechSettings());

        // The send mode is kept by a save that does not name it, and an unknown one is refused.
        Assert.AreEqual(SpeechSettings.ManualSend, off.Send);
        var sending = await fixture.Service.SaveSettingsAsync(new("host", false, "whisper-small") { Send = SpeechSettings.SendOnStop }, default);
        Assert.AreEqual(("ok", SpeechSettings.SendOnStop), (sending.Status, sending.Send));
        Assert.AreEqual(SpeechSettings.SendOnStop, (await fixture.Service.SaveSettingsAsync(new("host", false, "whisper-small"), default)).Send);
        Assert.AreEqual(new SpeechSettings(false, "whisper-small", SpeechSettings.SendOnStop), new CodeAltaConfigStore(fixture.Options).LoadGlobalSpeechSettings());
        var unknown = await fixture.Service.SaveSettingsAsync(new("host", false, "whisper-small") { Send = "after_silence" }, default);
        Assert.AreEqual(("invalid_request", SpeechSettings.SendOnStop), (unknown.Status, unknown.Send));

        foreach (var invalid in new[] { "", "not-a-preset", "https://example.com/owner/model", "https://huggingface.co/owner" })
        {
            var refused = await fixture.Service.SaveSettingsAsync(new("host", true, invalid), default);
            Assert.AreEqual("invalid_request", refused.Status, invalid);
            Assert.IsNotNull(refused.Error);
            Assert.AreEqual("whisper-small", refused.Model, "what was saved stays");
        }

        var removed = await fixture.Service.DeleteAsync(new("host", "whisper-small"), default);
        Assert.AreEqual("ok", removed.Status);
        Assert.IsFalse(removed.Models.Single(static model => model.Id == "whisper-small").Installed);
        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new("host", "never-installed"), default)).Status);
    }

    [TestMethod]
    public async Task Dictation_NeedsTheModel_ThenStreamsItsTextAndStops()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(new SpeechStartResponse("model_missing", null), await fixture.Service.StartAsync(new("host"), default));
        Assert.AreEqual(0, fixture.Started.Count, "no worker starts without a model");

        InstallModel(fixture.SpeechRoot, Runtime, SpeechSettings.DefaultModel, SpeechSettings.DefaultModel);
        var started = await fixture.Service.StartAsync(new("host"), default);
        Assert.AreEqual("ok", started.Status);
        await Until(() => fixture.Engine.Loads.Count == 1, "the model to load");
        var load = fixture.Engine.Loads[0];
        Assert.AreEqual(Path.Combine(fixture.SpeechRoot, "models", SpeechSettings.DefaultModel), load.ModelDirectory);
        Assert.AreEqual(Path.Combine(fixture.SpeechRoot, "vad", "silero_vad.onnx"), load.VoiceDetector);
        // The engine installed where speech kept it before is moved to the folder search shares with it.
        Assert.AreEqual(Path.Combine(fixture.Options.RuntimeRoot, "sherpa-onnx", SpeechModelCatalog.EngineVersion, Runtime), load.EngineDirectory);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.SpeechRoot, "runtime", SpeechModelCatalog.EngineVersion, Runtime)));
        CollectionAssert.AreEqual(new[] { "onnxruntime.dll", "sherpa-onnx-c-api.dll" }, load.EngineLibraries);
        Assert.AreEqual(("encoder.onnx", "tokens.txt"), (load.Files["encoder"], load.Files["tokens"]));
        Assert.IsTrue(load.Threads is >= 2 and <= 4);

        var transcripts = new List<SpeechTranscript>();
        var listening = Task.Run(async () =>
        {
            await foreach (var transcript in fixture.Service.ListenAsync(new("host", started.Id), default))
            {
                lock (transcripts) transcripts.Add(transcript);
            }
        });
        Assert.AreEqual("ok", fixture.Service.Append(new("host", started.Id, 0, Convert.ToBase64String(Audio(8, loud: true)))).Status);
        Assert.AreEqual("ok", fixture.Service.Append(new("host", started.Id, 1, Convert.ToBase64String(Audio(2, loud: false)))).Status);
        Assert.AreEqual("unknown", fixture.Service.Append(new("host", "another", 0, "AAAA")).Status);
        Assert.AreEqual("invalid_request", fixture.Service.Append(new("host", started.Id, 2, "not base64!")).Status);
        Assert.AreEqual("invalid_request", fixture.Service.Append(new("host", started.Id, -1, "AAAA")).Status);
        Assert.AreEqual("invalid_request", fixture.Service.Append(new("host", started.Id, 2, new string('A', 500_000))).Status, "ten seconds in one call is not a chunk");
        await Until(() => { lock (transcripts) return transcripts.Any(static transcript => transcript.FinalText == "10 windows"); }, "the first utterance");
        Assert.AreEqual("ok", fixture.Service.Append(new("host", started.Id, 2, Convert.ToBase64String(Audio(4, loud: true)))).Status);

        Assert.AreEqual(new SpeechStopResponse("ok", "10 windows 4 windows", null), await fixture.Service.StopAsync(new("host", started.Id, false), default));
        await listening.WaitAsync(TimeSpan.FromSeconds(10));
        lock (transcripts) Assert.AreEqual(new SpeechTranscript("done", "10 windows 4 windows", "", null), transcripts[^1], "the stream ends with the dictation, on its whole text");
        Assert.AreEqual("unknown", (await fixture.Service.StopAsync(new("host", "another", false), default)).Status);
        Assert.AreEqual(0, await CountAsync(fixture.Service.ListenAsync(new("host", "another"), default)));

        // Turned off, nothing starts and the model leaves the memory; so does the model that is no longer chosen.
        await fixture.Service.SaveSettingsAsync(new("host", false, SpeechSettings.DefaultModel), default);
        await Until(() => fixture.Started[0].Killed, "the worker to end");
        Assert.AreEqual("disabled", (await fixture.Service.StartAsync(new("host"), default)).Status);
        await fixture.Service.SaveSettingsAsync(new("host", true, SpeechSettings.DefaultModel), default);
        var again = await fixture.Service.StartAsync(new("host"), default);
        Assert.AreEqual(2, fixture.Started.Count);
        var removed = await fixture.Service.DeleteAsync(new("host", SpeechSettings.DefaultModel), default);
        Assert.AreEqual("ok", removed.Status);
        Assert.IsTrue(fixture.Started[1].Killed, "the worker lets go of the files of a model that is removed");
        Assert.AreEqual("ok", (await fixture.Service.StopAsync(new("host", again.Id, false), default)).Status);
        Assert.AreEqual("model_missing", (await fixture.Service.StartAsync(new("host"), default)).Status);
    }

    [TestMethod]
    public async Task Watch_ReplaysTheStateOfTheDownload()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await foreach (var state in fixture.Service.WatchAsync(new("host"), cancellation.Token))
        {
            Assert.AreEqual(new SpeechInstallState("idle", null, null, 0, 0, null), state);
            break;
        }

        Assert.AreEqual("invalid_request", (await fixture.Service.InstallAsync(new("host", "not-a-preset"), default)).Status);
        Assert.AreEqual("ok", fixture.Service.CancelInstall(new("host")).Status);
        var unsupported = new SpeechService(fixture.Options, "host", null, _ => throw new InvalidOperationException(), TimeProvider.System, null);
        Assert.AreEqual("unsupported", (await unsupported.InstallAsync(new("host", null), default)).Status);
        Assert.AreEqual("unsupported", (await unsupported.StartAsync(new("host"), default)).Status);
        Assert.IsFalse((await unsupported.ReadAsync(new("host"), default)).Supported);
        Assert.IsFalse(unsupported.IsMicrophoneAllowed());
        await unsupported.CloseAsync();
    }

    [TestMethod]
    public void Microphone_IsAllowedOnceToTheApplicationOnly_AndTheMacBundleSaysWhy()
    {
        var page = new Uri("app://codealta/index.html");
        Assert.AreEqual(NeoPermissionDecision.AllowOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.Microphone, page), microphoneAllowed: true));
        Assert.AreEqual(NeoPermissionDecision.AllowOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.Microphone, new Uri("app://codealta")), microphoneAllowed: true), "the origin of the page, as the views give it");
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.Microphone, null), microphoneAllowed: true), "a request that does not say who asks is refused");
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.Microphone, page), microphoneAllowed: false));
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.Microphone, new Uri("https://example.com/")), microphoneAllowed: true));
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.Microphone, new Uri("app://other/index.html")), microphoneAllowed: true));
        // The installed fonts are listed for the code font of Settings → Appearance, to the application only.
        Assert.AreEqual(NeoPermissionDecision.AllowOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.LocalFonts, page), microphoneAllowed: false));
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.LocalFonts, null), microphoneAllowed: false));
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.LocalFonts, new Uri("https://example.com/")), microphoneAllowed: false));
        // The clipboard is read for the Paste of the editors' context menu (#269), by the application only.
        Assert.AreEqual(NeoPermissionDecision.AllowOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.ClipboardRead, page), microphoneAllowed: false));
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.ClipboardRead, null), microphoneAllowed: false));
        Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(NeoPermissionKind.ClipboardRead, new Uri("https://example.com/")), microphoneAllowed: false));
        foreach (var kind in Enum.GetValues<NeoPermissionKind>().Where(static kind => kind is not (NeoPermissionKind.Microphone or NeoPermissionKind.LocalFonts or NeoPermissionKind.ClipboardRead)))
        {
            Assert.AreEqual(NeoPermissionDecision.DenyOnce, DesktopApplication.DecidePermission(new(kind, page), microphoneAllowed: true), kind.ToString());
        }

        // macOS asks the user with this sentence, and refuses the microphone to a bundle that has none.
        var plist = System.Xml.Linq.XDocument.Parse(DesktopIntegration.MacInfoPlist(null));
        var keys = plist.Descendants("key").ToList();
        var reason = keys.Single(static key => key.Value == "NSMicrophoneUsageDescription").ElementsAfterSelf().First();
        Assert.AreEqual("string", reason.Name.LocalName);
        StringAssert.Contains(reason.Value, "microphone");
    }

    private static async Task<int> CountAsync<T>(IAsyncEnumerable<T> items)
    {
        var count = 0;
        await foreach (var _ in items) count++;
        return count;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = NewRoot();
            Options = new() { GlobalRoot = Root };
            Service = new(Options, "host", null, _ =>
            {
                var process = new FakeSpeechWorkerProcess(Engine);
                lock (Started) Started.Add(process);
                return process;
            }, new SpeechTestClock(), Runtime);
        }

        public string Root { get; }
        public CatalogOptions Options { get; }
        public string SpeechRoot => Options.SpeechRoot;
        public FakeSpeechEngine Engine { get; } = new();
        public List<FakeSpeechWorkerProcess> Started { get; } = [];
        public SpeechService Service { get; }

        public void Dispose()
        {
            Service.CloseAsync().GetAwaiter().GetResult();
            foreach (var process in Started) process.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
