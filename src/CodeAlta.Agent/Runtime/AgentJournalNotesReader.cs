using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

// Latest-notes scanning over an already opened journal. No filesystem acquisition, locking or caching here.
internal static class AgentJournalNotesReader
{
    internal const int GuardBytes = 256;
    private const int ReadBytes = 128 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A journal prefix whose records were all read: <paramref name="Latest"/> is the last notes event in <c>[0, Offset)</c>.</summary>
    /// <param name="Offset">Exclusive end of the prefix, immediately after a line terminator.</param>
    /// <param name="LastWriteUtcTicks">Journal last-write time when the prefix was read.</param>
    /// <param name="Guard">The bytes ending at <paramref name="Offset"/>, used to recognize the same prefix in a longer journal.</param>
    /// <param name="Latest">The last notes event in the prefix, or null.</param>
    internal sealed record Prefix(long Offset, long LastWriteUtcTicks, byte[] Guard, AgentNotesEvent? Latest);

    /// <summary>A scan outcome. <paramref name="Supported"/> is false for BOM-detected non-UTF-8 journals, which only the tolerant text reader decodes.</summary>
    internal readonly record struct Result(bool Supported, AgentNotesEvent? Latest, Prefix? Prefix);

    /// <summary>Reads the latest notes event in <c>[0, length)</c>, continuing after <paramref name="previous"/> when the journal still starts with it.</summary>
    /// <remarks>
    /// Matches the tolerant line reader: every record is deserialized, blank lines are skipped and only a malformed final record is ignored.
    /// A final record that is malformed or unterminated is never part of the returned prefix, so it is read again once the journal grows.
    /// The guard recognizes appends; like the history stamps it cannot detect every in-place rewrite of older records.
    /// </remarks>
    /// <exception cref="JsonException">A record before the final one is malformed.</exception>
    /// <exception cref="InvalidDataException">A notes event has no Markdown, or a record exceeds the maximum array length.</exception>
    /// <exception cref="OperationCanceledException">The scan is canceled.</exception>
    internal static async Task<Result> ScanAsync(Stream stream, Prefix? previous, long length, long lastWriteUtcTicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var end = Math.Min(length, stream.Length);
        var start = 0L;
        AgentNotesEvent? latest = null;
        byte[]? guard = null;
        // Same length with another write time is a rewrite, not an append: read it from the start.
        if (previous is { Offset: > 0 } && previous.Offset < end
            && (await ReadGuardAsync(stream, previous.Offset, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(previous.Guard))
        {
            start = previous.Offset;
            latest = previous.Latest;
            guard = previous.Guard;
        }
        else
        {
            var bom = new byte[4];
            stream.Position = 0;
            var count = await FillAsync(stream, bom.AsMemory(0, (int)Math.Min(bom.Length, end)), cancellationToken).ConfigureAwait(false);
            if ((count >= 2 && ((bom[0] == 0xfe && bom[1] == 0xff) || (bom[0] == 0xff && bom[1] == 0xfe))) ||
                (count == 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xfe && bom[3] == 0xff))
            {
                return default;
            }

            if (count >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf)
            {
                start = 3;
            }
        }

        var verified = start;
        var verifiedLatest = latest;
        var position = start;
        var filled = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);
        try
        {
            stream.Position = start;
            while (position + filled < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (filled == buffer.Length)
                {
                    // One record is longer than the buffer.
                    if (buffer.Length >= Array.MaxLength) throw new InvalidDataException("A journal record is too large to read.");
                    var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(buffer.Length * 2L, Array.MaxLength));
                    buffer.AsSpan(0, filled).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(filled, (int)Math.Min(buffer.Length - filled, end - position - filled)), cancellationToken).ConfigureAwait(false);
                // The journal became shorter after its length was captured; what was read is all there is.
                if (read == 0) end = position + filled;
                filled += read;
                var consumed = Consume(buffer.AsSpan(0, filled), position, end, ref latest, ref verified, ref verifiedLatest);
                buffer.AsSpan(consumed, filled - consumed).CopyTo(buffer);
                position += consumed;
                filled -= consumed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (guard is null || verified != start)
        {
            guard = await ReadGuardAsync(stream, verified, cancellationToken).ConfigureAwait(false);
        }

        // A short guard read means the journal was truncated underneath the scan; answer without remembering it.
        return new(true, latest, guard.Length == Math.Min(GuardBytes, verified) ? new Prefix(verified, lastWriteUtcTicks, guard, verifiedLatest) : null);
    }

    // Consumes every complete line and, once the buffer reaches the scan end, the unterminated remainder.
    private static int Consume(ReadOnlySpan<byte> buffer, long position, long end, ref AgentNotesEvent? latest, ref long verified, ref AgentNotesEvent? verifiedLatest)
    {
        var atEnd = position + buffer.Length == end;
        var consumed = 0;
        while (consumed < buffer.Length)
        {
            var rest = buffer[consumed..];
            var lineLength = rest.IndexOfAny((byte)'\n', (byte)'\r');
            var terminated = lineLength >= 0;
            var next = buffer.Length;
            if (terminated)
            {
                next = consumed + lineLength + 1;
                if (rest[lineLength] == (byte)'\r')
                {
                    // The line feed completing this terminator may not have been read yet.
                    if (next == buffer.Length && !atEnd) break;
                    if (next < buffer.Length && buffer[next] == (byte)'\n') next++;
                }
            }
            else if (atEnd)
            {
                lineLength = rest.Length;
            }
            else
            {
                break;
            }

            var final = position + next == end;
            consumed = next;
            AgentEvent? value;
            try
            {
                value = Parse(rest[..lineLength]);
            }
            catch (JsonException) when (final)
            {
                // The tolerated incomplete tail. It stays outside the verified prefix.
                break;
            }

            if (value is AgentNotesEvent notes)
            {
                if (notes.Markdown is null)
                {
                    throw new InvalidDataException("A notes journal event has no Markdown value.");
                }

                latest = notes;
            }

            if (terminated)
            {
                verified = position + next;
                verifiedLatest = latest;
            }
        }

        return consumed;
    }

    // Returns null for a blank line.
    private static AgentEvent? Parse(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, AgentJsonSerializerContext.Default.AgentEvent)
                ?? throw new JsonException("Journal line deserialized to null.");
        }
        catch (JsonException)
        {
            // Decide exactly as the text reader does: it skips whitespace-only lines and replaces invalid UTF-8.
            var text = Utf8WithoutBom.GetString(line);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonSerializer.Deserialize(text, AgentJsonSerializerContext.Default.AgentEvent)
                ?? throw new JsonException("Journal line deserialized to null.");
        }
    }

    private static async Task<byte[]> ReadGuardAsync(Stream stream, long offset, CancellationToken cancellationToken)
    {
        var guard = new byte[(int)Math.Min(GuardBytes, offset)];
        stream.Position = offset - guard.Length;
        var count = await FillAsync(stream, guard, cancellationToken).ConfigureAwait(false);
        return count == guard.Length ? guard : guard[..count];
    }

    private static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[count..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }

        return count;
    }
}
