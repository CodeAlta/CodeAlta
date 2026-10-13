using System.Runtime.InteropServices;
using System.Text.Json;
using CodeAlta.Desktop.Speech;
using static CodeAlta.Desktop.Tests.SpeechTestSupport;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SpeechWorkerTests
{
    [TestMethod]
    public void Protocol_RoundTripsFrames_AndRefusesWhatIsNotOne()
    {
        using var stream = new MemoryStream();
        SpeechWorkerProtocol.Write(stream, SpeechFrameKind.Audio, 7, [1, 2, 3]);
        SpeechWorkerProtocol.Write(stream, SpeechFrameKind.Final, 7, "Bonjour, ça va ?");
        SpeechWorkerProtocol.Write(stream, SpeechFrameKind.Done, 7, []);
        stream.Position = 0;
        var audio = SpeechWorkerProtocol.Read(stream)!.Value;
        Assert.AreEqual((SpeechFrameKind.Audio, 7), (audio.Kind, audio.Dictation));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, audio.Content);
        Assert.AreEqual("Bonjour, ça va ?", SpeechWorkerProtocol.Read(stream)!.Value.Text);
        Assert.AreEqual(SpeechFrameKind.Done, SpeechWorkerProtocol.Read(stream)!.Value.Kind);
        Assert.IsNull(SpeechWorkerProtocol.Read(stream), "the end of the stream is not an error");

        // What a library would print on the same stream is not taken for a frame.
        Assert.Throws<InvalidDataException>(() => SpeechWorkerProtocol.Read(new MemoryStream("some log line\n"u8.ToArray())));
        var tooLong = new byte[] { 0xA7, 2, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F };
        Assert.Throws<InvalidDataException>(() => SpeechWorkerProtocol.Read(new MemoryStream(tooLong)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechWorkerProtocol.Write(new MemoryStream(), SpeechFrameKind.Audio, 1, new byte[SpeechWorkerProtocol.MaximumContent + 1]));
    }

    [TestMethod]
    public void Pipeline_ShowsAnUtteranceAsItGrows_ThenGivesItOnce()
    {
        var recognizer = new FakeSpeechRecognizer();
        var pipeline = new SpeechDictationPipeline(recognizer, new FakeSpeechDetector());
        var finals = new List<string>();

        // Before speech, nothing is decoded and only the last windows are kept.
        pipeline.Accept(Samples(40, loud: false), finals.Add);
        Assert.IsNull(pipeline.Partial());
        Assert.AreEqual(0, recognizer.Decodes);

        // Speech: the text so far, with what was kept from before it, and only once enough was added.
        pipeline.Accept(Samples(8, loud: true), finals.Add);
        Assert.AreEqual("18 windows", pipeline.Partial(), "10 windows kept from before the speech, 8 of speech");
        Assert.IsNull(pipeline.Partial(), "nothing new since the last one");
        pipeline.Accept(Samples(2, loud: true), finals.Add);
        Assert.IsNull(pipeline.Partial(), "0.064 s more is not worth another decoding");
        pipeline.Accept(Samples(6, loud: true), finals.Add);
        Assert.AreEqual("26 windows", pipeline.Partial());
        Assert.AreEqual(0, finals.Count);

        // A pause ends the utterance: decoded once more, as the detector cut it.
        pipeline.Accept(Samples(2, loud: false), finals.Add);
        CollectionAssert.AreEqual(new[] { "18 windows" }, finals, "16 windows of speech and the 2 quiet ones that ended it");
        Assert.IsNull(pipeline.Partial(), "the next utterance has not started");

        // The end of the dictation gives the utterance still being spoken.
        pipeline.Accept(Samples(4, loud: true), finals.Add);
        pipeline.Finish(finals.Add);
        CollectionAssert.AreEqual(new[] { "18 windows", "4 windows" }, finals);
        pipeline.Finish(finals.Add);
        Assert.AreEqual(2, finals.Count, "a dictation that ended has nothing more to give");
    }

    [TestMethod]
    public async Task Worker_LoadsDecodesAndClosesADictation_AndSurvivesAModelItCannotLoad()
    {
        var engine = new FakeSpeechEngine();
        using var process = new FakeSpeechWorkerProcess(engine);
        var frames = new List<SpeechFrame>();
        var reading = Task.Run(() =>
        {
            while (SpeechWorkerProtocol.Read(process.Output) is { } frame)
            {
                lock (frames) frames.Add(frame);
            }
        });
        SpeechFrame[] Frames() { lock (frames) return [.. frames]; }

        // Audio before a model is ignored; a model the engine refuses is an error, not the end of the worker.
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Audio, 1, Audio(4, loud: true));
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Load, 0, Load("broken"));
        await Until(() => Frames().Any(static frame => frame.Kind == SpeechFrameKind.Error), "the load error");
        Assert.AreEqual(0, Frames().Single().Dictation);
        StringAssert.Contains(Frames().Single().Text, "did not load");

        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Load, 0, Load("good"));
        await Until(() => Frames().Any(static frame => frame.Kind == SpeechFrameKind.Loaded), "the model");
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Audio, 3, Audio(8, loud: true));
        await Until(() => Frames().Any(static frame => frame.Kind == SpeechFrameKind.Partial), "the text so far");
        Assert.AreEqual((3, "8 windows"), (Frames()[^1].Dictation, Frames()[^1].Text));
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Audio, 3, Audio(2, loud: false));
        await Until(() => Frames().Any(static frame => frame.Kind == SpeechFrameKind.Final), "the utterance");
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Audio, 3, Audio(4, loud: true));
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Finish, 3, []);
        await Until(() => Frames().Any(static frame => frame.Kind == SpeechFrameKind.Done), "the end of the dictation");
        CollectionAssert.AreEqual(new[] { "10 windows", "4 windows" }, Frames().Where(static frame => frame.Kind == SpeechFrameKind.Final).Select(static frame => frame.Text).ToArray());
        Assert.AreEqual(3, Frames()[^1].Dictation);

        // A canceled dictation gives nothing, and the next one starts clean.
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Audio, 4, Audio(6, loud: true));
        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Cancel, 4, []);
        await Until(() => Frames().Count(static frame => frame.Kind == SpeechFrameKind.Done) == 2, "the canceled dictation");
        Assert.AreEqual(2, Frames().Count(static frame => frame.Kind == SpeechFrameKind.Final));

        SpeechWorkerProtocol.Write(process.Input, SpeechFrameKind.Exit, 0, []);
        await reading.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => process.Ended, "the worker to end");
        Assert.IsTrue(engine.Recognizer!.Disposed, "the model is released when the worker ends");
    }

    [TestMethod]
    public async Task Client_KeepsAudioInOrder_GivesTheWholeText_AndReusesTheWorker()
    {
        var engine = new FakeSpeechEngine();
        var started = new List<FakeSpeechWorkerProcess>();
        var client = new SpeechWorkerClient(_ => Track(started, new(engine)), new SpeechTestClock(), TimeSpan.FromMinutes(5));
        try
        {
            var dictation = client.Start(LoadOf("good"));
            Assert.IsTrue(dictation.Current.State is "loading" or "listening");
            var seen = new List<SpeechTranscriptSnapshot>();
            using var subscription = dictation.Subscribe(snapshot => { lock (seen) seen.Add(snapshot); });

            // The calls of the page run concurrently: the second chunk arrives first and waits for the first.
            Assert.IsTrue(client.Append(dictation.Id, 1, Audio(4, loud: true)));
            Assert.IsTrue(client.Append(dictation.Id, 0, Audio(4, loud: true)));
            Assert.IsFalse(client.Append(dictation.Id, 0, Audio(4, loud: true)), "a chunk already given is not given twice");
            Assert.IsFalse(client.Append("other", 2, Audio(1, loud: true)));
            await Until(() => dictation.Current.PartialText == "8 windows", "the text so far");
            Assert.AreEqual("listening", dictation.Current.State);
            Assert.IsTrue(client.Append(dictation.Id, 2, Audio(2, loud: false)));
            await Until(() => dictation.Current.FinalText == "10 windows", "the first utterance");
            Assert.AreEqual("", dictation.Current.PartialText, "a finished utterance is no longer the one being spoken");
            Assert.IsTrue(client.Append(dictation.Id, 3, Audio(3, loud: true)));

            var last = await client.StopAsync(dictation.Id, cancel: false, default);
            Assert.AreEqual(new SpeechTranscriptSnapshot("done", "10 windows 3 windows", "", null), last);
            Assert.IsFalse(client.Append(dictation.Id, 4, Audio(1, loud: true)), "a dictation that ended takes no audio");
            Assert.AreEqual(last, await client.StopAsync(dictation.Id, cancel: false, default), "stopping twice gives the same text");
            lock (seen) Assert.AreEqual("done", seen[^1].State);

            // The next dictation finds the model loaded, in the same worker.
            var second = client.Start(LoadOf("good"));
            Assert.AreEqual("listening", second.Current.State);
            Assert.IsNull(client.Find(dictation.Id));
            Assert.AreSame(second, client.Find(second.Id));
            Assert.IsTrue(client.Append(second.Id, 0, Audio(5, loud: true)));
            Assert.AreEqual("", (await client.StopAsync(second.Id, cancel: true, default))!.FinalText, "canceled, what was being spoken is dropped");
            Assert.AreEqual(1, started.Count);
            Assert.AreEqual(1, engine.Loads.Count);
        }
        finally { await client.CloseAsync(); }

        Assert.IsTrue(started.All(static process => process.Killed));
    }

    [TestMethod]
    public async Task Client_ReportsAModelTheEngineRefuses_AndAWorkerThatDies()
    {
        var engine = new FakeSpeechEngine();
        var started = new List<FakeSpeechWorkerProcess>();
        var client = new SpeechWorkerClient(_ => Track(started, new(engine)), new SpeechTestClock(), TimeSpan.FromMinutes(5));
        try
        {
            Assert.AreEqual("load_failed", await client.ProbeAsync(LoadOf("broken"), default));
            var refused = client.Start(LoadOf("broken"));
            Assert.AreEqual(new SpeechTranscriptSnapshot("failed", "", "", "load_failed"), await refused.Over.WaitAsync(TimeSpan.FromSeconds(10)));

            // Another model starts another worker; the engine closing its process ends the dictation with an error.
            Assert.IsNull(await client.ProbeAsync(LoadOf("good"), default));
            var dictation = client.Start(LoadOf("good"));
            Assert.AreEqual(2, started.Count);
            Assert.IsTrue(client.Append(dictation.Id, 0, Audio(8, loud: true)));
            await Until(() => dictation.Current.PartialText.Length > 0, "the text so far");
            started[^1].Kill();
            Assert.AreEqual("engine_failed", (await dictation.Over.WaitAsync(TimeSpan.FromSeconds(10))).Error);
            await Until(() => client.ModelDirectory is null, "the dead worker to be forgotten");
            Assert.AreEqual("failed", (await client.StopAsync(dictation.Id, cancel: false, default))!.State);

            // The next dictation starts a new worker.
            var next = client.Start(LoadOf("good"));
            Assert.AreEqual(3, started.Count);
            await Until(() => next.Current.State == "listening", "the new worker");
        }
        finally { await client.CloseAsync(); }
    }

    [TestMethod]
    public async Task Client_EndsAnIdleWorker_AndAWorkerThatCannotStartFailsTheDictation()
    {
        var engine = new FakeSpeechEngine();
        var clock = new SpeechTestClock();
        var started = new List<FakeSpeechWorkerProcess>();
        var client = new SpeechWorkerClient(_ => Track(started, new(engine)), clock, TimeSpan.FromMinutes(5));
        try
        {
            var dictation = client.Start(LoadOf("good"));
            await client.StopAsync(dictation.Id, cancel: true, default);
            Assert.IsNotNull(client.ModelDirectory);
            clock.Advance(TimeSpan.FromMinutes(4));
            Assert.IsNotNull(client.ModelDirectory, "the model stays loaded for the next dictation");

            // A dictation started meanwhile keeps the worker past the first deadline.
            var again = client.Start(LoadOf("good"));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.IsNotNull(client.ModelDirectory);
            await client.StopAsync(again.Id, cancel: true, default);
            clock.Advance(TimeSpan.FromMinutes(5));
            await Until(() => client.ModelDirectory is null && started[0].Killed, "the idle worker to end");
            Assert.AreEqual(1, started.Count);
        }
        finally { await client.CloseAsync(); }

        var failing = new SpeechWorkerClient(_ => throw new IOException("no executable"), clock, TimeSpan.FromMinutes(5));
        Assert.AreEqual(new SpeechTranscriptSnapshot("failed", "", "", "engine_failed"), failing.Start(LoadOf("good")).Current);
        Assert.AreEqual("engine_failed", await failing.ProbeAsync(LoadOf("good"), default));
        await failing.CloseAsync();
        Assert.AreEqual("failed", failing.Start(LoadOf("good")).Current.State, "a closed client starts nothing");
    }

    private static FakeSpeechWorkerProcess Track(List<FakeSpeechWorkerProcess> started, FakeSpeechWorkerProcess process)
    {
        lock (started) started.Add(process);
        return process;
    }

    private static short[] Samples(int windows, bool loud) => MemoryMarshal.Cast<byte, short>(Audio(windows, loud)).ToArray();

    private static SpeechWorkerLoad LoadOf(string name) => new() { ModelDirectory = Path.Combine(Path.GetTempPath(), name), Family = nameof(SpeechModelFamily.Whisper) };

    private static byte[] Load(string name) => JsonSerializer.SerializeToUtf8Bytes(LoadOf(name), SpeechJsonContext.Default.SpeechWorkerLoad);
}
