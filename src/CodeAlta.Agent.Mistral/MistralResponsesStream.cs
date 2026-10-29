using System.Buffers;

namespace CodeAlta.Agent.Mistral;

/// <summary>
/// Passes the stream of an answer of the Responses API of Mistral through, with its times in whole seconds: Mistral
/// streams <c>created_at</c> and <c>completed_at</c> with a fraction (<c>1793304532.0</c>), which the OpenAI client
/// does not read (live probe of 2026-10-29). Each line goes on as soon as it ends: an event of the stream is not held.
/// </summary>
internal sealed class MistralResponsesStream(Stream inner) : Stream
{
    private readonly ArrayBufferWriter<byte> _line = new();
    private readonly ArrayBufferWriter<byte> _ready = new();
    private int _readyOffset;
    private bool _ended;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var chunk = ArrayPool<byte>.Shared.Rent(Math.Max(buffer.Length, 4096));
        try
        {
            while (Pending == 0 && !_ended)
            {
                Accept(chunk.AsSpan(0, inner.Read(chunk, 0, chunk.Length)));
            }

            return Take(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var chunk = ArrayPool<byte>.Shared.Rent(Math.Max(buffer.Length, 4096));
        try
        {
            while (Pending == 0 && !_ended)
            {
                var read = await inner.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                Accept(chunk.AsSpan(0, read));
            }

            return Take(buffer.Span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the fraction of the numbers of the properties named <c>..._at</c> in one line of JSON, in place.
    /// A name inside a string is escaped (<c>\"created_at\"</c>) and is left as it is.
    /// </summary>
    /// <returns>The new length of the line.</returns>
    internal static int TruncateTimes(Span<byte> line)
    {
        var length = line.Length;
        var search = 0;
        while (search < length)
        {
            var found = line[search..length].IndexOf("_at\":"u8);
            if (found < 0)
            {
                break;
            }

            var nameEnd = search + found;
            var cursor = nameEnd + 5;
            search = cursor;
            if (!IsPropertyName(line[..nameEnd]))
            {
                continue;
            }

            while (cursor < length && line[cursor] == (byte)' ')
            {
                cursor++;
            }

            var digits = cursor;
            while (cursor < length && char.IsAsciiDigit((char)line[cursor]))
            {
                cursor++;
            }

            if (cursor == digits || cursor >= length || line[cursor] != (byte)'.')
            {
                continue;
            }

            var end = cursor + 1;
            while (end < length && char.IsAsciiDigit((char)line[end]))
            {
                end++;
            }

            // A number with an exponent is not a time in seconds as Mistral writes it: left as it is.
            if (end == cursor + 1 || (end < length && (line[end] is (byte)'e' or (byte)'E')))
            {
                continue;
            }

            line[end..length].CopyTo(line[cursor..]);
            length -= end - cursor;
            search = cursor;
        }

        return length;
    }

    // The bytes before "_at\":" end with the rest of a property name: letters, digits and '_' after an opening quote
    // that is not escaped.
    private static bool IsPropertyName(ReadOnlySpan<byte> before)
    {
        var index = before.Length - 1;
        while (index >= 0 && (char.IsAsciiLetterOrDigit((char)before[index]) || before[index] == (byte)'_'))
        {
            index--;
        }

        return index >= 0 && before[index] == (byte)'"' && (index == 0 || before[index - 1] != (byte)'\\');
    }

    private int Pending => _ready.WrittenCount - _readyOffset;

    private void Accept(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            _ended = true;
            if (_line.WrittenCount > 0)
            {
                Complete();
            }

            return;
        }

        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 0)
            {
                _line.Write(bytes);
                return;
            }

            _line.Write(bytes[..(newline + 1)]);
            Complete();
            bytes = bytes[(newline + 1)..];
        }
    }

    private void Complete()
    {
        if (_readyOffset == _ready.WrittenCount)
        {
            _ready.ResetWrittenCount();
            _readyOffset = 0;
        }

        var line = _ready.GetSpan(_line.WrittenCount)[.._line.WrittenCount];
        _line.WrittenSpan.CopyTo(line);
        _ready.Advance(TruncateTimes(line));
        _line.ResetWrittenCount();
    }

    private int Take(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, Pending);
        _ready.WrittenSpan.Slice(_readyOffset, count).CopyTo(buffer);
        _readyOffset += count;
        return count;
    }
}
