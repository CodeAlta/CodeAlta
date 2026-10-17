using System.Buffers.Binary;
using System.Text;

namespace CodeAlta.NativeEngines;

/// <summary>One message between a host and its worker: its kind, the operation it belongs to (zero for none) and its content.</summary>
/// <param name="Kind">The kind, defined by the protocol of the worker.</param>
/// <param name="Correlation">The operation it belongs to, or zero.</param>
/// <param name="Content">The content.</param>
public readonly record struct WorkerFrame(byte Kind, int Correlation, byte[] Content)
{
    /// <summary>Gets the content as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Content);
}

/// <summary>
/// Frames on a byte stream: a marker, the kind, the correlation, the length of the content, the content. The
/// marker tells a frame from anything a native library would write on the same stream.
/// </summary>
public static class WorkerFrames
{
    /// <summary>The largest content of a frame.</summary>
    public const int MaximumContent = 4 * 1024 * 1024;

    private const byte Marker = 0xA7;
    private const int HeaderLength = 10;

    /// <summary>Writes one frame and flushes it.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="correlation">The operation it belongs to.</param>
    /// <param name="content">The content, at most <see cref="MaximumContent"/> bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The content is too long.</exception>
    /// <exception cref="IOException">The other side is gone.</exception>
    public static void Write(Stream stream, byte kind, int correlation, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, MaximumContent);
        Span<byte> header = stackalloc byte[HeaderLength];
        header[0] = Marker;
        header[1] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], correlation);
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], content.Length);
        stream.Write(header);
        stream.Write(content);
        stream.Flush();
    }

    /// <summary>Reads one frame, or null at the end of the stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The frame, or null when the stream ended.</returns>
    /// <exception cref="InvalidDataException">The stream does not hold a frame.</exception>
    public static WorkerFrame? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderLength];
        var read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);
        if (read == 0) return null;
        if (read < HeaderLength || header[0] != Marker) throw new InvalidDataException("The worker stream does not hold a frame.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[6..]);
        if (length is < 0 or > MaximumContent) throw new InvalidDataException("The worker stream holds a frame that is too long.");
        var content = new byte[length];
        stream.ReadExactly(content);
        return new(header[1], BinaryPrimitives.ReadInt32LittleEndian(header[2..]), content);
    }
}
