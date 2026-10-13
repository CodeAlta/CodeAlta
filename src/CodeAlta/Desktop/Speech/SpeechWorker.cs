using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodeAlta.Desktop.Speech;

/// <summary>Turns the samples of an utterance into text.</summary>
internal interface ISpeechRecognizer : IDisposable
{
    /// <summary>Decodes 16 kHz mono samples.</summary>
    string Decode(float[] samples);
}

/// <summary>Finds where speech starts and ends in a stream of samples.</summary>
internal interface ISpeechVoiceDetector : IDisposable
{
    /// <summary>Gets the number of samples it takes at a time.</summary>
    int WindowSize { get; }

    /// <summary>Gets whether speech is being heard.</summary>
    bool IsSpeech { get; }

    /// <summary>Takes one window of samples.</summary>
    void Accept(float[] window);

    /// <summary>Takes the next finished utterance, when there is one.</summary>
    bool TryTake(out float[] samples);

    /// <summary>Ends the utterance being heard, so that it can be taken.</summary>
    void Flush();

    /// <summary>Forgets what was heard.</summary>
    void Reset();
}

/// <summary>Loads a model into a recognizer and a voice detector.</summary>
internal interface ISpeechEngine
{
    /// <summary>Loads the model.</summary>
    /// <exception cref="InvalidOperationException">The engine does not load it.</exception>
    (ISpeechRecognizer Recognizer, ISpeechVoiceDetector Detector) Load(SpeechWorkerLoad load);
}

/// <summary>
/// One dictation with a recognizer that reads whole utterances: the voice detector cuts the audio where the
/// speaker pauses, a finished utterance is decoded once, and the one being spoken is decoded again as it grows
/// to show what it reads as so far. This is how sherpa-onnx shows live text with a model that is not streaming.
/// </summary>
internal sealed class SpeechDictationPipeline(ISpeechRecognizer recognizer, ISpeechVoiceDetector detector)
{
    /// <summary>The audio added to an utterance before it is decoded again: 0.2 s.</summary>
    internal const int PartialInterval = 3200;

    // What is kept of the audio before speech: the detector hears speech a few windows after it starts.
    private const int WindowsKeptBeforeSpeech = 10;

    private readonly List<float> _buffer = [];
    private int _offset;
    private int _decoded;
    private bool _speaking;

    /// <summary>Takes audio and reports the utterances it ends.</summary>
    public void Accept(ReadOnlySpan<short> samples, Action<string> final)
    {
        foreach (var sample in samples) _buffer.Add(sample / 32768f);
        var window = detector.WindowSize;
        for (; _offset + window <= _buffer.Count; _offset += window)
        {
            detector.Accept(_buffer.GetRange(_offset, window).ToArray());
            if (!_speaking && detector.IsSpeech) _speaking = true;
        }

        if (!_speaking && _buffer.Count > WindowsKeptBeforeSpeech * window)
        {
            var removed = _buffer.Count - WindowsKeptBeforeSpeech * window;
            _buffer.RemoveRange(0, removed);
            _offset -= removed;
        }

        Take(final);
    }

    /// <summary>
    /// Decodes the utterance being spoken when enough was added to it. The caller asks when it has nothing else
    /// to do, so a slow computer shows this text less often and never falls behind the voice.
    /// </summary>
    /// <returns>The text so far, or null when there is nothing new.</returns>
    public string? Partial()
    {
        if (!_speaking || _buffer.Count - _decoded < PartialInterval) return null;
        _decoded = _buffer.Count;
        return Clean(recognizer.Decode([.. _buffer]));
    }

    /// <summary>Ends the dictation: the utterance being spoken is decoded as it is.</summary>
    public void Finish(Action<string> final)
    {
        detector.Flush();
        Take(final);
        Reset();
    }

    /// <summary>Forgets the dictation.</summary>
    public void Reset()
    {
        detector.Reset();
        _buffer.Clear();
        (_offset, _decoded, _speaking) = (0, 0, false);
    }

    private void Take(Action<string> final)
    {
        while (detector.TryTake(out var utterance))
        {
            var text = Clean(recognizer.Decode(utterance));
            if (text.Length > 0) final(text);
            _buffer.Clear();
            (_offset, _decoded, _speaking) = (0, 0, false);
        }
    }

    private static string Clean(string? text) => (text ?? "").Trim();
}

/// <summary>
/// The speech worker: the process the recognizer runs in, so that a model the engine refuses, which ends the
/// process it runs in, never ends the application. It answers frames on its standard streams until they close.
/// </summary>
internal static class SpeechWorker
{
    /// <summary>The argument that starts this executable as the worker.</summary>
    public const string Option = "--speech-worker";

    /// <summary>Runs the worker on the standard streams of the process.</summary>
    public static int RunProcess()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Run(input, output, new SherpaSpeechEngine());
        return 0;
    }

    /// <summary>Answers the frames of a stream until it ends or asks to exit.</summary>
    public static void Run(Stream input, Stream output, ISpeechEngine engine)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(engine);
        // Frames are read as they come, also while a model loads or an utterance is decoded: the host never
        // waits to write, and the loop sees whether more audio is already there.
        var frames = new BlockingCollection<SpeechFrame?>();
        var reader = new Thread(() =>
        {
            try
            {
                while (SpeechWorkerProtocol.Read(input) is { } frame) frames.Add(frame);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
            {
            }
            finally
            {
                frames.Add(null);
            }
        }) { IsBackground = true, Name = "speech-frames" };
        reader.Start();

        ISpeechRecognizer? recognizer = null;
        ISpeechVoiceDetector? detector = null;
        SpeechDictationPipeline? pipeline = null;
        var dictation = 0;
        try
        {
            while (true)
            {
                SpeechFrame? next;
                if (!frames.TryTake(out next))
                {
                    // Nothing waits: the moment to show what the utterance reads as so far.
                    if (pipeline?.Partial() is { } partial) SpeechWorkerProtocol.Write(output, SpeechFrameKind.Partial, dictation, partial);
                    next = frames.Take();
                }

                if (next is not { } frame || frame.Kind == SpeechFrameKind.Exit) return;
                try
                {
                    switch (frame.Kind)
                    {
                        case SpeechFrameKind.Load:
                            pipeline = null;
                            recognizer?.Dispose();
                            detector?.Dispose();
                            (recognizer, detector) = (null, null);
                            var load = JsonSerializer.Deserialize(frame.Content, SpeechJsonContext.Default.SpeechWorkerLoad) ?? throw new InvalidOperationException("No model was given.");
                            (recognizer, detector) = engine.Load(load);
                            pipeline = new(recognizer, detector);
                            SpeechWorkerProtocol.Write(output, SpeechFrameKind.Loaded, 0, []);
                            break;
                        case SpeechFrameKind.Audio when pipeline is not null:
                            if (frame.Dictation != dictation)
                            {
                                pipeline.Reset();
                                dictation = frame.Dictation;
                            }

                            pipeline.Accept(MemoryMarshal.Cast<byte, short>(frame.Content.AsSpan(0, frame.Content.Length & ~1)),
                                text => SpeechWorkerProtocol.Write(output, SpeechFrameKind.Final, dictation, text));
                            break;
                        case SpeechFrameKind.Finish or SpeechFrameKind.Cancel:
                            if (pipeline is not null && frame.Dictation == dictation && frame.Kind == SpeechFrameKind.Finish)
                                pipeline.Finish(text => SpeechWorkerProtocol.Write(output, SpeechFrameKind.Final, dictation, text));
                            pipeline?.Reset();
                            SpeechWorkerProtocol.Write(output, SpeechFrameKind.Done, frame.Dictation, []);
                            break;
                    }
                }
                catch (Exception exception) when (exception is not IOException)
                {
                    SpeechWorkerProtocol.Write(output, SpeechFrameKind.Error, frame.Dictation, exception.GetType().Name + ": " + exception.Message);
                }
            }
        }
        catch (IOException)
        {
            // The host is gone.
        }
        finally
        {
            recognizer?.Dispose();
            detector?.Dispose();
        }
    }
}
