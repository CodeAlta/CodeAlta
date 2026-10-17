using System.IO.Pipes;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Tests;

/// <summary>Engines that give a vector from the length of each text, and that a test can hold.</summary>
internal sealed class FakeEngines(int dimensions = 4, bool words = false) : IEmbeddingEngineFactory
{
    private readonly int _dimensions = dimensions;
    private readonly bool _words = words;
    private int _loads;

    // A vector of the words of a text, the prompts left out: texts that share words are close.
    private float[] Words(string text)
    {
        var vector = new float[_dimensions];
        var body = text[(text.LastIndexOf('|') + 1)..];
        foreach (var word in body.Split([' ', ',', '.', ':', '?'], StringSplitOptions.RemoveEmptyEntries))
            vector[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(word) % (uint)_dimensions)] += 1;
        var norm = MathF.Sqrt(vector.Sum(static value => value * value));
        if (norm > 0) for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        return vector;
    }

    public int Loads => Volatile.Read(ref _loads);
    public ManualResetEventSlim Gate { get; } = new(true);
    public SemaphoreSlim Started { get; } = new(0);
    public System.Collections.Concurrent.ConcurrentQueue<string> Texts { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<int> LoadedThreads { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string[]> Batches { get; } = new();
    private int _embedding;
    private int _mostAtOnce;

    /// <summary>Gets the most batches embedded at the same time.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    public IEmbeddingEngine Load(EmbeddingWorkerLoad load)
    {
        if (load.ModelId == "broken") throw new InvalidOperationException("The model did not load.");
        Interlocked.Increment(ref _loads);
        LoadedThreads.Enqueue(load.Threads);
        return new Engine(this);
    }

    private sealed class Engine(FakeEngines owner) : IEmbeddingEngine
    {
        public int Dimensions => owner._dimensions;

        public float[][] Embed(IReadOnlyList<string> texts)
        {
            var now = Interlocked.Increment(ref owner._embedding);
            for (var most = Volatile.Read(ref owner._mostAtOnce); now > most && Interlocked.CompareExchange(ref owner._mostAtOnce, now, most) != most; most = Volatile.Read(ref owner._mostAtOnce)) { }
            try
            {
                owner.Started.Release();
                owner.Gate.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Interlocked.Decrement(ref owner._embedding);
            }

            if (texts.Contains("refuse")) throw new InvalidOperationException("The text was refused.");
            owner.Batches.Enqueue([.. texts]);
            foreach (var text in texts) owner.Texts.Enqueue(text);
            return owner._words ? [.. texts.Select(owner.Words)] : [.. texts.Select(static text => new[] { text.Length, 1f, 0f, 0f })];
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>The real worker loop on a thread, joined to its host by two pipes.</summary>
internal sealed class FakeEmbeddingWorker : IWorkerProcess
{
    private readonly AnonymousPipeServerStream _hostWrites = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _workerWrites = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _workerReads;
    private readonly AnonymousPipeClientStream _hostReads;
    private readonly Thread _thread;

    public FakeEmbeddingWorker(IEmbeddingEngineFactory engines)
    {
        _workerReads = new(PipeDirection.In, _hostWrites.ClientSafePipeHandle);
        _hostReads = new(PipeDirection.In, _workerWrites.ClientSafePipeHandle);
        _thread = new(() =>
        {
            try { EmbeddingWorker.Run(_workerReads, _workerWrites, engines); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            finally { Close(_workerWrites); }
        }) { IsBackground = true };
        _thread.Start();
    }

    public Stream Input => _hostWrites;
    public Stream Output => _hostReads;
    public bool Ended => !_thread.IsAlive;

    public void Kill()
    {
        Close(_workerWrites);
        Close(_hostWrites);
    }

    public void Dispose()
    {
        _thread.Join(TimeSpan.FromSeconds(5));
        Kill();
        Close(_workerReads);
        Close(_hostReads);
    }

    private static void Close(Stream stream)
    {
        try { stream.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
