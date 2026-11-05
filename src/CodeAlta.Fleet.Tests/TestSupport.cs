using System.Threading.Channels;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace CodeAlta.Fleet.Tests;

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>A folder of its own for one test, deleted after it.</summary>
internal sealed class TestFolder : IDisposable
{
    public TestFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-fleet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One end of an in-memory duplex connection: what one end writes, the other reads.</summary>
internal sealed class InMemoryDuplexStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private ReadOnlyMemory<byte> _pending;

    private InMemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (InMemoryDuplexStream First, InMemoryDuplexStream Second) CreatePair()
    {
        var firstToSecond = Channel.CreateUnbounded<byte[]>();
        var secondToFirst = Channel.CreateUnbounded<byte[]>();
        return (new InMemoryDuplexStream(secondToFirst, firstToSecond), new InMemoryDuplexStream(firstToSecond, secondToFirst));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_pending.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_incoming.Reader.TryRead(out var chunk))
            {
                _pending = chunk;
            }
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!buffer.IsEmpty && !_outgoing.Writer.TryWrite(buffer.ToArray()))
        {
            throw new IOException("The other end is closed.");
        }

        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Closing one end closes the connection: the other end reads its end and can no longer write.
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        base.Dispose(disposing);
    }
}
