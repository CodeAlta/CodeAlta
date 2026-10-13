using System.Runtime.InteropServices;
using SherpaOnnx;

namespace CodeAlta.Desktop.Speech;

/// <summary>
/// The engine of the speech worker: sherpa-onnx, whose native libraries are in the cache and not beside the
/// application. Only the worker process uses this type.
/// </summary>
/// <remarks>
/// The wrapper gives the engine its paths in the ANSI code page of Windows while the engine reads UTF-8, so a
/// path with an accent would not be found. The worker therefore works in the folder of the model and names
/// every file relative to it: model ids and file names are plain ASCII.
/// </remarks>
internal sealed class SherpaSpeechEngine : ISpeechEngine
{
    private const string Library = "sherpa-onnx-c-api";
    private IntPtr _library;

    /// <inheritdoc />
    public (ISpeechRecognizer Recognizer, ISpeechVoiceDetector Detector) Load(SpeechWorkerLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        Resolve(load);
        Directory.SetCurrentDirectory(load.ModelDirectory);
        foreach (var file in load.Files.Values)
        {
            if (!File.Exists(file)) throw new InvalidOperationException($"The model file '{file}' is missing.");
        }

        var detector = Path.GetRelativePath(load.ModelDirectory, load.VoiceDetector);
        if (!File.Exists(detector)) throw new InvalidOperationException("The voice detection model is missing.");
        return Create(load, detector);
    }

    private void Resolve(SpeechWorkerLoad load)
    {
        if (_library != IntPtr.Zero) return;
        // The libraries the engine needs first, by their full path: the engine then finds them already loaded
        // whatever the search path of the system is.
        foreach (var library in load.EngineLibraries)
        {
            _library = NativeLibrary.Load(Path.Combine(load.EngineDirectory, library));
        }

        if (_library == IntPtr.Zero) throw new InvalidOperationException("The speech engine is missing.");
        var handle = _library;
        NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, (name, _, _) => name == Library ? handle : IntPtr.Zero);
    }

    private static (ISpeechRecognizer, ISpeechVoiceDetector) Create(SpeechWorkerLoad load, string detector)
    {
        string File(string role) => load.Files.TryGetValue(role, out var name) ? name : throw new InvalidOperationException($"The model has no {role} file.");
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.NumThreads = Math.Clamp(load.Threads, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Tokens = File("tokens");
        config.DecodingMethod = "greedy_search";
        switch (Enum.Parse<SpeechModelFamily>(load.Family))
        {
            case SpeechModelFamily.NemoTransducer:
            case SpeechModelFamily.Transducer:
                config.ModelConfig.Transducer.Encoder = File("encoder");
                config.ModelConfig.Transducer.Decoder = File("decoder");
                config.ModelConfig.Transducer.Joiner = File("joiner");
                // Named, a NeMo model is not loaded a second time only to read its kind; another transducer
                // says its kind in the encoder.
                if (load.Family == nameof(SpeechModelFamily.NemoTransducer)) config.ModelConfig.ModelType = "nemo_transducer";
                break;
            case SpeechModelFamily.MoonshineV1:
                config.ModelConfig.Moonshine.Preprocessor = File("preprocessor");
                config.ModelConfig.Moonshine.Encoder = File("encoder");
                config.ModelConfig.Moonshine.UncachedDecoder = File("uncachedDecoder");
                config.ModelConfig.Moonshine.CachedDecoder = File("cachedDecoder");
                break;
            case SpeechModelFamily.MoonshineV2:
                config.ModelConfig.Moonshine.Encoder = File("encoder");
                config.ModelConfig.Moonshine.MergedDecoder = File("mergedDecoder");
                break;
            case SpeechModelFamily.Whisper:
                config.ModelConfig.Whisper.Encoder = File("encoder");
                config.ModelConfig.Whisper.Decoder = File("decoder");
                config.ModelConfig.Whisper.Task = "transcribe";
                break;
        }

        var recognizer = new Recognizer(config);
        var vad = new VadModelConfig();
        vad.SileroVad.Model = detector;
        // A pause of 0.4 s ends an utterance; one that goes on is cut after 15 s, which bounds the audio decoded
        // again for the text shown while it is spoken.
        vad.SileroVad.MinSilenceDuration = 0.4f;
        vad.SileroVad.MaxSpeechDuration = 15f;
        return (recognizer, new Detector(vad));
    }

    private sealed class Recognizer : ISpeechRecognizer
    {
        private readonly OfflineRecognizer _recognizer;

        public Recognizer(OfflineRecognizerConfig config)
        {
            _recognizer = new(config);
            // The wrapper keeps a null recognizer without a word: a stream of it is null too.
            using var probe = _recognizer.CreateStream();
            if (probe.Handle == IntPtr.Zero)
            {
                _recognizer.Dispose();
                throw new InvalidOperationException("The engine did not load the model.");
            }
        }

        public string Decode(float[] samples)
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text ?? "";
        }

        public void Dispose() => _recognizer.Dispose();
    }

    private sealed class Detector(VadModelConfig config) : ISpeechVoiceDetector
    {
        // Utterances wait here until they are taken: a minute is several of the longest.
        private readonly VoiceActivityDetector _detector = new(config, 60);

        public int WindowSize { get; } = config.SileroVad.WindowSize;

        public bool IsSpeech => _detector.IsSpeechDetected();

        public void Accept(float[] window) => _detector.AcceptWaveform(window);

        public bool TryTake(out float[] samples)
        {
            samples = [];
            if (_detector.IsEmpty()) return false;
            samples = _detector.Front().Samples;
            _detector.Pop();
            return true;
        }

        public void Flush() => _detector.Flush();

        public void Reset() => _detector.Reset();

        public void Dispose() => _detector.Dispose();
    }
}
