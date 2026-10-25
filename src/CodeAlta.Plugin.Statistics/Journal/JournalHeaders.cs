using System.Buffers;
using System.Text.Json;
using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>Reads what a session's header, the first line of its journal, says, without reading the rest of the journal.</summary>
internal static class JournalHeaders
{
    /// <summary>The most bytes read for a first line: a header is a few hundred bytes, and a longer first line is no header.</summary>
    public const int MaxHeaderBytes = 1024 * 1024;

    /// <summary>Reads the key of the folder a session works in (<see cref="SessionFolders.Key"/>) from its header.</summary>
    /// <param name="stream">A readable stream of the journal, at its start.</param>
    /// <returns>The key; null when the first line is not a complete header, or records no folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="IOException">The journal could not be read.</exception>
    public static string? ReadFolderKey(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var line = new ArrayBufferWriter<byte>(4096);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    // A first line that is not finished yet is no header yet.
                    return null;
                }

                var span = buffer.AsSpan(0, read);
                var newline = span.IndexOf((byte)'\n');
                var piece = newline >= 0 ? span[..newline] : span;
                if (line.WrittenCount + piece.Length > MaxHeaderBytes)
                {
                    return null;
                }

                line.Write(piece);
                if (newline >= 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var text = line.WrittenSpan;
        if (text.StartsWith("﻿"u8))
        {
            text = text[3..];
        }

        text = text.TrimEnd((byte)'\r');
        if (JournalPrefixClassifier.Classify(text[..Math.Min(text.Length, JournalPrefixClassifier.PrefixBytes)], out _) != JournalRecordKind.Header)
        {
            return null;
        }

        try
        {
            return new JournalPayloadParser(new Utf8StringCache()).Parse(JournalRecordKind.Header, text, isFinalBlock: true, null) is HeaderRecord header ? header.FolderKey : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
