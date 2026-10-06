using System.Collections.Concurrent;
using System.Text;
using CodeAlta.Desktop.Terminals;

namespace CodeAlta.Desktop.Tests;

/// <summary>A program behind a terminal that shows what a test tells it to and records what it is sent.</summary>
internal sealed class FakeTerminalProgram : PseudoTerminal
{
    private readonly BlockingCollection<byte[]> _shown = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _typed = new();
    private byte[] _rest = [];

    public ConcurrentQueue<(int Columns, int Rows)> Sizes { get; } = new();
    public bool Killed { get; private set; }
    public override int ProcessId => 4242;
    public override Task<int> Exited => _exited.Task;

    public string Typed
    {
        get { lock (_typed) return _typed.ToString(); }
    }

    public void Show(string text) => _shown.Add(Encoding.UTF8.GetBytes(text));

    public void End(int code)
    {
        _exited.TrySetResult(code);
        _shown.CompleteAdding();
    }

    public override int Read(Span<byte> buffer)
    {
        if (_rest.Length == 0)
        {
            try { _rest = _shown.Take(); }
            catch (InvalidOperationException) { return 0; }
        }
        var count = Math.Min(buffer.Length, _rest.Length);
        _rest.AsSpan(0, count).CopyTo(buffer);
        _rest = _rest[count..];
        return count;
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_typed) _typed.Append(Encoding.UTF8.GetString(data));
    }

    public override void Resize(int columns, int rows) => Sizes.Enqueue((columns, rows));

    public override void Kill()
    {
        Killed = true;
        End(137);
    }

    public override void Dispose() => End(-1);
}
