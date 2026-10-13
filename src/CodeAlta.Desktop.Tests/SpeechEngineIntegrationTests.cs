using System.Buffers.Binary;
using System.Diagnostics;
using CodeAlta.Catalog.Speech;
using CodeAlta.Desktop.Speech;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The real engine, on request: downloads a model into a folder and has the real worker process read a WAV
/// file. It needs the network and hundreds of megabytes, so it runs only when its variables are set:
/// <list type="bullet">
/// <item><c>CODEALTA_SPEECH_TEST_ROOT</c>: a speech cache folder (as <c>~/.alta/cache/speech</c>), kept between runs;</item>
/// <item><c>CODEALTA_SPEECH_TEST_WAV</c>: a 16 kHz mono 16-bit WAV file;</item>
/// <item><c>CODEALTA_SPEECH_TEST_MODEL</c>: a preset id or an address, the default model when absent;</item>
/// <item><c>CODEALTA_SPEECH_TEST_WORDS</c>: words the text must contain, separated by commas.</item>
/// </list>
/// Run it after a change of the version of sherpa-onnx: the wrapper and the libraries must be read the same way.
/// </summary>
[TestClass]
public sealed class SpeechEngineIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60 * 60 * 1000, CooperativeCancellation = true)]
    public async Task RealWorker_ReadsAWavFile()
    {
        var root = Environment.GetEnvironmentVariable("CODEALTA_SPEECH_TEST_ROOT");
        var wav = Environment.GetEnvironmentVariable("CODEALTA_SPEECH_TEST_WAV");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(wav))
        {
            Assert.Inconclusive("Set CODEALTA_SPEECH_TEST_ROOT and CODEALTA_SPEECH_TEST_WAV to run the real speech engine.");
            return;
        }

        var model = Environment.GetEnvironmentVariable("CODEALTA_SPEECH_TEST_MODEL") is { Length: > 0 } chosen ? chosen : SpeechSettings.DefaultModel;
        var installer = new SpeechInstaller(root, null, SpeechModelCatalog.FindEngine(SpeechModelCatalog.CurrentRuntimeIdentifier()), SpeechModelCatalog.VoiceDetector);
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "alta.exe" : "alta");
        Assert.IsTrue(File.Exists(executable), "the desktop executable is beside the tests");
        var client = new SpeechWorkerClient(directory => SpeechWorkerProcess.Start(executable, directory), TimeProvider.System, TimeSpan.FromMinutes(5));
        try
        {
            Assert.IsTrue(installer.TryStart(model));
            while (installer.Current.IsRunning) await Task.Delay(500, TestContext.CancellationToken);
            Assert.AreEqual("done", installer.Current.State, installer.Current.Error);
            var installed = installer.Find(SpeechModelSource.ModelIdOf(model))!;
            TestContext.WriteLine($"Model {installed.Manifest.Name} ({installed.Manifest.Family}), {installed.Bytes / 1048576} MB in {installed.Directory}");

            var load = Stopwatch.StartNew();
            Assert.IsNull(await client.ProbeAsync(installer.LoadFor(installed), TestContext.CancellationToken), "the engine loads the model");
            TestContext.WriteLine($"Loaded in {load.ElapsedMilliseconds} ms");

            var samples = ReadWav(wav);
            var dictation = client.Start(installer.LoadFor(installed));
            var partials = new List<string>();
            using var subscription = dictation.Subscribe(snapshot =>
            {
                lock (partials)
                {
                    if (snapshot.PartialText.Length > 0 && (partials.Count == 0 || partials[^1] != snapshot.PartialText)) partials.Add(snapshot.PartialText);
                }
            });
            var decode = Stopwatch.StartNew();
            var sequence = 0;
            // As the page sends it: a tenth of a second at a time, at the speed of speech.
            for (var offset = 0; offset < samples.Length; offset += 3200)
            {
                Assert.IsTrue(client.Append(dictation.Id, sequence++, samples.AsSpan(offset, Math.Min(3200, samples.Length - offset)).ToArray()));
                await Task.Delay(100, TestContext.CancellationToken);
            }

            var last = await client.StopAsync(dictation.Id, cancel: false, TestContext.CancellationToken);
            TestContext.WriteLine($"Audio {samples.Length / 32000.0:0.0} s, done in {decode.ElapsedMilliseconds} ms");
            lock (partials) TestContext.WriteLine($"{partials.Count} partial texts, the last: {(partials.Count > 0 ? partials[^1] : "")}");
            TestContext.WriteLine("Text: " + last!.FinalText);
            Assert.AreEqual("done", last.State, last.Error);
            Assert.IsTrue(last.FinalText.Length > 0, "the engine read something");
            foreach (var word in (Environment.GetEnvironmentVariable("CODEALTA_SPEECH_TEST_WORDS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                StringAssert.Contains(last.FinalText.ToLowerInvariant(), word.ToLowerInvariant());
            }
        }
        finally
        {
            await client.CloseAsync();
            await installer.CloseAsync();
        }
    }

    private static byte[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.IsTrue(bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8), "a WAV file");
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (bytes.AsSpan(at, 4).SequenceEqual("fmt "u8))
            {
                Assert.AreEqual((1, 1, 16000, 16), ((int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 8)), (int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 10)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 12)), (int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 22))), "16 kHz mono 16-bit PCM");
            }
            else if (bytes.AsSpan(at, 4).SequenceEqual("data"u8))
            {
                // A second of silence after the speech, so that the last utterance ends by itself too.
                return [.. bytes.AsSpan(at + 8, Math.Min(size, bytes.Length - at - 8) & ~1), .. new byte[32000]];
            }

            at += 8 + size + (size & 1);
        }

        Assert.Fail("The WAV file has no audio.");
        return [];
    }
}
