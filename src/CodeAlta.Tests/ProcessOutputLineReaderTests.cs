using System.Diagnostics;
using System.Text;
using CodeAlta.Agent.Claude;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

/// <summary>
/// The reader of a redirected output of a process gives, on a thread of its own (as on Windows), what an asynchronous
/// read of the stream gives elsewhere: the same lines, in order, the end, the errors, and cancellable waits.
/// </summary>
[TestClass]
public sealed class ProcessOutputLineReaderTests
{
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    [TestMethod]
    [DataRow("")]
    [DataRow("one")]
    [DataRow("one\ntwo\n")]
    [DataRow("one\r\ntwo\rthree\n\nfive")]
    [DataRow("\n\n\r\n")]
    [DataRow("é 漢字 😀\nnext")]
    public async Task DedicatedThread_ReadsTheLinesTheStreamReaderReads(string text)
    {
        CollectionAssert.AreEqual(ReadWithStreamReader(text), await ReadAllAsync(text, dedicatedThread: true));
        CollectionAssert.AreEqual(ReadWithStreamReader(text), await ReadAllAsync(text, dedicatedThread: false));
    }

    [TestMethod]
    public async Task DedicatedThread_ReadsLongLinesAndManyLinesInOrder()
    {
        var text = string.Join('\n', Enumerable.Range(0, 2000).Select(static index => index % 100 == 0
            ? new string((char)('a' + index % 26), 10_000) + "é"
            : index.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        CollectionAssert.AreEqual(ReadWithStreamReader(text), await ReadAllAsync(text, dedicatedThread: true));
    }

    [TestMethod]
    public async Task DedicatedThread_IsABackgroundThreadOfItsOwn()
    {
        var stream = new GatedStream();
        using var reader = ProcessOutputLineReader.Start(new StreamReader(stream), "test output", dedicatedThread: true);
        stream.Push("line\n");
        stream.End();

        Assert.IsTrue(reader.UsesDedicatedThread);
        Assert.AreEqual("line", await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        Assert.IsNull(await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        var (isBackground, isThreadPoolThread, name) = stream.ReadingThread!.Value;
        Assert.IsTrue(isBackground);
        Assert.IsFalse(isThreadPoolThread);
        Assert.AreEqual("test output", name);
        Assert.IsFalse(ProcessOutputLineReader.Start(new StreamReader(new MemoryStream()), "unused", dedicatedThread: false).UsesDedicatedThread);
    }

    [TestMethod]
    public async Task CancelledWait_ReturnsWhileTheThreadReads_AndLosesNoLine()
    {
        var stream = new GatedStream();
        using var reader = ProcessOutputLineReader.Start(new StreamReader(stream), "test output", dedicatedThread: true);
        using var cancellation = new CancellationTokenSource();
        var waiting = reader.ReadLineAsync(cancellation.Token).AsTask();

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting.WaitAsync(Hang));

        stream.Push("after\n");
        Assert.AreEqual("after", await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        stream.End();
        Assert.IsNull(await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
    }

    [TestMethod]
    public async Task ErrorOfARead_ComesAfterTheLinesReadBeforeIt()
    {
        var stream = new GatedStream();
        using var reader = ProcessOutputLineReader.Start(new StreamReader(stream), "test output", dedicatedThread: true);
        stream.Push("one\ntwo\n");
        stream.Fail(new IOException("The pipe broke."));

        Assert.AreEqual("one", await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        Assert.AreEqual("two", await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        var error = await Assert.ThrowsAsync<IOException>(async () => await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        Assert.AreEqual("The pipe broke.", error.Message);
    }

    [TestMethod]
    public async Task Dispose_EndsAWaitingRead_AfterTheLinesReadAlready()
    {
        var stream = new GatedStream();
        var reader = ProcessOutputLineReader.Start(new StreamReader(stream), "test output", dedicatedThread: true);
        stream.Push("kept\n");
        await stream.WaitForReadsAsync(2).WaitAsync(Hang);

        reader.Dispose();
        Assert.AreEqual("kept", await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        Assert.IsNull(await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        stream.End();
    }

    [TestMethod]
    public async Task DedicatedThread_ReadsAtMostItsReadAheadBeforeTheConsumer()
    {
        var stream = new GatedStream();
        using var reader = ProcessOutputLineReader.Start(new StreamReader(stream), "test output", dedicatedThread: true);
        for (var index = 0; index < 100; index++)
        {
            stream.Push(index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        // One chunk is one line: the thread holds ReadAheadLines lines, and one more it waits to hand over.
        await stream.WaitForReadsAsync(ProcessOutputLineReader.ReadAheadLines + 1).WaitAsync(Hang);
        await Task.Delay(100);
        Assert.AreEqual(ProcessOutputLineReader.ReadAheadLines + 1, stream.ReadCount);

        for (var index = 0; index < 100; index++)
        {
            Assert.AreEqual(index.ToString(System.Globalization.CultureInfo.InvariantCulture), await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        }

        stream.End();
        Assert.IsNull(await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ClaudeCodeProcessTransport_ReadsTheOutputsOfTheProcess(bool dedicatedReaderThreads)
    {
        if (OperatingSystem.IsWindows())
        {
            // The command below is a POSIX shell's; on Windows the production path itself reads on threads of their own.
            return;
        }

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("echo started >&2; while read line; do echo \"got $line\"; done; echo ended >&2");
        var process = Process.Start(startInfo)!;
        var transport = new ClaudeCodeProcessTransport(process, dedicatedReaderThreads);
        try
        {
            await transport.WriteLineAsync(Encoding.UTF8.GetBytes("{\"a\":1}"), CancellationToken.None).AsTask().WaitAsync(Hang);
            Assert.AreEqual("got {\"a\":1}", await transport.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        }
        finally
        {
            await transport.DisposeAsync().AsTask().WaitAsync(Hang);
        }

        Assert.IsNull(await transport.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang));
        Assert.AreEqual("started" + Environment.NewLine + "ended", transport.StandardErrorTail);
    }

    private static string[] ReadWithStreamReader(string text)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    private static async Task<string[]> ReadAllAsync(string text, bool dedicatedThread)
    {
        // A stream that gives a few bytes at a time, as a pipe gives what was written.
        var stream = new GatedStream(maxChunk: 7);
        stream.Push(text);
        stream.End();
        using var reader = ProcessOutputLineReader.Start(new StreamReader(stream, Encoding.UTF8), "test output", dedicatedThread);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(Hang) is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    // A blocking stream fed by the test, one pushed chunk per read at most, as a synchronous pipe.
    private sealed class GatedStream(int maxChunk = int.MaxValue) : Stream
    {
        private readonly object _sync = new();
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _current;
        private int _offset;
        private bool _ended;
        private Exception? _failure;
        private int _readCount;

        public (bool IsBackground, bool IsThreadPoolThread, string? Name)? ReadingThread { get; private set; }

        public int ReadCount
        {
            get
            {
                lock (_sync)
                {
                    return _readCount;
                }
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Push(string text)
        {
            lock (_sync)
            {
                _chunks.Enqueue(Encoding.UTF8.GetBytes(text));
                Monitor.PulseAll(_sync);
            }
        }

        public void End()
        {
            lock (_sync)
            {
                _ended = true;
                Monitor.PulseAll(_sync);
            }
        }

        public void Fail(Exception failure)
        {
            lock (_sync)
            {
                _failure = failure;
                Monitor.PulseAll(_sync);
            }
        }

        public async Task WaitForReadsAsync(int count)
        {
            while (ReadCount < count)
            {
                await Task.Delay(5);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                ReadingThread ??= (Thread.CurrentThread.IsBackground, Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name);
                _readCount++;
                while (true)
                {
                    if (_current is null && _chunks.TryDequeue(out var next))
                    {
                        _current = next;
                        _offset = 0;
                    }

                    if (_current is not null)
                    {
                        var length = Math.Min(Math.Min(count, maxChunk), _current.Length - _offset);
                        Array.Copy(_current, _offset, buffer, offset, length);
                        _offset += length;
                        if (_offset == _current.Length)
                        {
                            _current = null;
                        }

                        return length;
                    }

                    if (_failure is not null)
                    {
                        throw _failure;
                    }

                    if (_ended)
                    {
                        return 0;
                    }

                    Monitor.Wait(_sync);
                }
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
