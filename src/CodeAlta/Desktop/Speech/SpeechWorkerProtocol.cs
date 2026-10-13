using System.Buffers.Binary;
using System.Text;

namespace CodeAlta.Desktop.Speech;

/// <summary>What the host and the speech worker say to each other.</summary>
internal enum SpeechFrameKind : byte
{
    /// <summary>Host: load a model. The content is a <see cref="SpeechWorkerLoad"/> in JSON.</summary>
    Load = 1,

    /// <summary>Host: audio of a dictation, 16 kHz mono 16-bit samples.</summary>
    Audio = 2,

    /// <summary>Host: the dictation is over; decode what remains and answer <see cref="Done"/>.</summary>
    Finish = 3,

    /// <summary>Host: forget the dictation and answer <see cref="Done"/>.</summary>
    Cancel = 4,

    /// <summary>Host: end the process.</summary>
    Exit = 5,

    /// <summary>Worker: the model is loaded.</summary>
    Loaded = 16,

    /// <summary>Worker: what the utterance being spoken reads as so far.</summary>
    Partial = 17,

    /// <summary>Worker: a finished utterance.</summary>
    Final = 18,

    /// <summary>Worker: the dictation is closed.</summary>
    Done = 19,

    /// <summary>Worker: something failed; the content says what, for the log.</summary>
    Error = 20,
}

/// <summary>One message: its kind, the dictation it belongs to (zero for none) and its content.</summary>
internal readonly record struct SpeechFrame(SpeechFrameKind Kind, int Dictation, byte[] Content)
{
    /// <summary>Gets the content as text.</summary>
    public string Text => Encoding.UTF8.GetString(Content);
}

/// <summary>The model a worker loads. Every path is absolute; the worker makes the ones it gives the engine relative.</summary>
internal sealed class SpeechWorkerLoad
{
    /// <summary>Gets or sets the folder of the native libraries of the engine.</summary>
    public string EngineDirectory { get; set; } = "";

    /// <summary>Gets or sets the libraries of the engine, in the order they are loaded.</summary>
    public List<string> EngineLibraries { get; set; } = [];

    /// <summary>Gets or sets the folder of the model.</summary>
    public string ModelDirectory { get; set; } = "";

    /// <summary>Gets or sets the name of a <see cref="SpeechModelFamily"/>.</summary>
    public string Family { get; set; } = "";

    /// <summary>Gets or sets the file names of the model by role.</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>Gets or sets the file of the model that finds where speech starts and ends.</summary>
    public string VoiceDetector { get; set; } = "";

    /// <summary>Gets or sets the number of threads of the recognizer.</summary>
    public int Threads { get; set; } = 2;
}

/// <summary>
/// Frames on a byte stream: a marker, the kind, the dictation, the length of the content, the content. The
/// marker tells a frame from anything a library would write on the same stream.
/// </summary>
internal static class SpeechWorkerProtocol
{
    /// <summary>The largest content: a model description or a second of audio is far below it.</summary>
    public const int MaximumContent = 4 * 1024 * 1024;

    private const byte Marker = 0xA7;
    private const int HeaderLength = 10;

    /// <summary>Writes one frame and flushes it.</summary>
    /// <exception cref="IOException">The other side is gone.</exception>
    public static void Write(Stream stream, SpeechFrameKind kind, int dictation, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, MaximumContent);
        Span<byte> header = stackalloc byte[HeaderLength];
        header[0] = Marker;
        header[1] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], dictation);
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], content.Length);
        stream.Write(header);
        stream.Write(content);
        stream.Flush();
    }

    /// <summary>Writes one frame whose content is text.</summary>
    public static void Write(Stream stream, SpeechFrameKind kind, int dictation, string text)
        => Write(stream, kind, dictation, Encoding.UTF8.GetBytes(text ?? ""));

    /// <summary>Reads one frame, or null at the end of the stream.</summary>
    /// <exception cref="InvalidDataException">The stream does not hold a frame.</exception>
    public static SpeechFrame? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderLength];
        var read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
        if (read == 0) return null;
        if (read < HeaderLength || header[0] != Marker) throw new InvalidDataException("The speech worker stream does not hold a frame.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[6..]);
        if (length is < 0 or > MaximumContent) throw new InvalidDataException("The speech worker stream holds a frame that is too long.");
        var content = new byte[length];
        stream.ReadExactly(content);
        return new((SpeechFrameKind)header[1], BinaryPrimitives.ReadInt32LittleEndian(header[2..]), content);
    }
}
