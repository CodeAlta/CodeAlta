using System.Diagnostics;
using CodeAlta.Catalog.Search;
using CodeAlta.Desktop.Search.Embedding;
using CodeAlta.NativeEngines;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>A chunk that has no vector yet in the active set: what the model embeds.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="EmbedHash">The hash of its text, which its vector is kept by.</param>
/// <param name="Title">The title of its item, for the document prompt.</param>
/// <param name="Text">Its text.</param>
internal sealed record MeaningChunk(long ChunkId, byte[] EmbedHash, string? Title, string Text);

/// <summary>The settings of search, as search by meaning reads and changes them.</summary>
internal interface IMeaningSettings
{
    /// <summary>Gets the settings now.</summary>
    SearchSettings GetSettings();

    /// <summary>Saves the settings and makes them current.</summary>
    void SaveSettings(SearchSettings settings);

    /// <summary>Raised when the settings changed.</summary>
    event Action<SearchSettings, SearchSettings>? SettingsChanged;
}

/// <summary>The vectors of search by meaning, as the window uses them: one set per model, in the index and in memory.</summary>
internal interface IMeaningVectors : IAsyncDisposable
{
    /// <summary>Opens the set of a model (made when it is new) and loads it in memory.</summary>
    Task OpenAsync(EmbeddingModelDefinition model, string runtimeVersion, CancellationToken cancellationToken);

    /// <summary>Gets chunks without a vector in the open set, the newest first.</summary>
    Task<IReadOnlyList<MeaningChunk>> MissingAsync(int count, CancellationToken cancellationToken);

    /// <summary>Keeps the vectors of chunks.</summary>
    Task PutAsync(IReadOnlyList<(MeaningChunk Chunk, float[] Vector)> vectors, CancellationToken cancellationToken);

    /// <summary>Finds the chunks closest to a query vector in a scope.</summary>
    ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(float[] query, SearchScope scope, int limit, CancellationToken cancellationToken);

    /// <summary>Gets the chunks with a vector and all the chunks, in the open set.</summary>
    (int Embedded, int Total) Coverage { get; }

    /// <summary>Reads again what the indexer changed: chunks written or removed since the last time.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the set of a model, its vectors with it.</summary>
    Task DeleteAsync(EmbeddingModelDefinition model, CancellationToken cancellationToken);
}

/// <summary>Where search by meaning is, for the page.</summary>
/// <param name="State"><c>off</c>, <c>downloading</c>, <c>loading</c>, <c>indexing</c>, <c>ready</c>, <c>paused</c> or <c>failed</c>.</param>
/// <param name="ModelId">The model chosen.</param>
/// <param name="Embedded">The chunks with a vector.</param>
/// <param name="Total">All the chunks.</param>
/// <param name="ChunksPerSecond">The speed of the last batches, or zero before.</param>
/// <param name="Error">For <c>failed</c>: what went wrong.</param>
internal sealed record MeaningStatus(string State, string ModelId, int Embedded, int Total, double ChunksPerSecond, string? Error)
{
    /// <summary>Gets an estimate of the seconds left to index, or null when unknown.</summary>
    public int? SecondsLeft => ChunksPerSecond > 0 && Total > Embedded ? (int)Math.Ceiling((Total - Embedded) / ChunksPerSecond) : null;
}

/// <summary>
/// Search by meaning in the window: the model it downloads, the worker it runs in, the vectors of the chunks of the index,
/// embedded in the background in batches (queries first), and the search of a query among the vectors in its scope.
/// It follows <c>[search] semantic</c> and <c>model</c>: turned off, the worker ends and nothing is embedded; the model
/// and the vectors stay, so that turning it on again is immediate.
/// </summary>
internal sealed class DesktopSearchMeaning : ISearchMeaning, IAsyncDisposable
{
    /// <summary>
    /// The chunks of a batch. A batch is padded to its longest text, and the memory of a worker follows its biggest
    /// batch: 8 texts of about the same length (sorted, see <see cref="EmbedMissingAsync"/>) embed as fast as 16 mixed
    /// ones with 40% less memory (EmbeddingGemma 2: about 1 GB a worker).
    /// </summary>
    internal const int BatchSize = 8;

    private readonly IMeaningSettings _host;
    private readonly SearchIndexer _indexer;
    private readonly EmbeddingInstaller _installer;
    private readonly IMeaningVectors _vectors;
    private readonly Func<EmbeddingWorkerLoad, EmbeddingClient> _client;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _loop = Task.CompletedTask;
    private CancellationTokenSource? _step;
    private EmbeddingClient? _embedding;
    private EmbeddingModelDefinition? _open;
    private string _state = "off";
    private string? _error;
    private bool _paused;
    private bool _dictating;
    private double _rate;
    private int _failures;
    // The start of a worker for queries, after the last one ended while idle (see SearchAsync).
    private Task _warmup = Task.CompletedTask;

    /// <summary>Creates search by meaning; <see cref="Start"/> starts it.</summary>
    /// <param name="host">The settings of search.</param>
    /// <param name="indexer">The indexer, whose writes leave chunks to embed.</param>
    /// <param name="installer">Downloads the model and the runtime.</param>
    /// <param name="vectors">The vectors.</param>
    /// <param name="client">Creates the client of a worker for a model.</param>
    /// <param name="log">Gets what is worth a line in the log.</param>
    public DesktopSearchMeaning(IMeaningSettings host, SearchIndexer indexer, EmbeddingInstaller installer, IMeaningVectors vectors,
        Func<EmbeddingWorkerLoad, EmbeddingClient> client, Action<string>? log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _vectors = vectors ?? throw new ArgumentNullException(nameof(vectors));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _log = log;
    }

    /// <summary>Raised when <see cref="Status"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Gets the installer, for the download the page follows.</summary>
    public EmbeddingInstaller Installer => _installer;

    /// <summary>Gets where search by meaning is.</summary>
    public MeaningStatus Status
    {
        get
        {
            var (embedded, total) = _open is null ? (0, 0) : _vectors.Coverage;
            lock (_gate) return new(_paused && _state is "indexing" ? "paused" : _state, _host.GetSettings().Model, embedded, total, _rate, _error);
        }
    }

    /// <inheritdoc />
    public SearchMeaningState State
    {
        get
        {
            lock (_gate)
            {
                return _state switch
                {
                    // Without a worker that has its model, a query would wait for one to start.
                    "ready" or "indexing" when Cold => SearchMeaningState.Warming,
                    "ready" => SearchMeaningState.Ready,
                    "indexing" => SearchMeaningState.Indexing,
                    "loading" => SearchMeaningState.Warming,
                    _ => SearchMeaningState.Off,
                };
            }
        }
    }

    /// <inheritdoc />
    public double Coverage
    {
        get
        {
            if (_open is null) return 0;
            var (embedded, total) = _vectors.Coverage;
            return total == 0 ? 1 : (double)embedded / total;
        }
    }

    /// <summary>Gets or sets whether the user paused the indexing.</summary>
    public bool Paused
    {
        get { lock (_gate) return _paused; }
        set
        {
            lock (_gate) _paused = value;
            ApplyPause();
            Notify();
        }
    }

    /// <summary>Tells that a dictation runs or ended: the indexing waits while one runs.</summary>
    public void SetDictating(bool dictating)
    {
        lock (_gate) _dictating = dictating;
        ApplyPause();
    }

    /// <summary>Starts following the settings; the work begins at once when search by meaning is on.</summary>
    public void Start()
    {
        _indexer.Changed += Wake;
        _host.SettingsChanged += OnSettingsChanged;
        _installer.Changed += _ => { Wake(); Notify(); };
        _loop = Task.Run(() => LoopAsync(_lifetime.Token));
        Wake();
    }

    /// <summary>Turns search by meaning on with a model: saves the choice, then downloads what is missing and indexes.</summary>
    public void TurnOn(string modelId)
    {
        var model = EmbeddingModelCatalog.Find(modelId) ?? throw new ArgumentException($"Unknown model '{modelId}'.", nameof(modelId));
        _host.SaveSettings(_host.GetSettings() with { Semantic = true, Model = model.Id, MeaningOffer = null });
        lock (_gate) (_error, _failures) = (null, 0);
        Wake();
    }

    /// <summary>Turns search by meaning off: the worker ends; the model and the vectors stay.</summary>
    public void TurnOff()
    {
        _host.SaveSettings(_host.GetSettings() with { Semantic = false, MeaningOffer = SearchSettings.MeaningOfferTurnedOff });
        Wake();
    }

    /// <summary>Removes a model and its vectors; refused for the model in use while search by meaning is on.</summary>
    /// <returns>True when it is gone.</returns>
    public async Task<bool> DeleteModelAsync(string modelId, CancellationToken cancellationToken)
    {
        if (EmbeddingModelCatalog.Find(modelId) is not { } model) return false;
        var settings = _host.GetSettings();
        if (settings.Semantic && string.Equals(settings.Model, model.Id, StringComparison.OrdinalIgnoreCase)) return false;
        await _vectors.DeleteAsync(model, cancellationToken).ConfigureAwait(false);
        var deleted = _installer.Delete(model);
        Notify();
        return deleted;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<(long ChunkId, float Similarity)>> SearchAsync(string query, SearchScope scope, bool codeQuery, int limit, CancellationToken cancellationToken)
    {
        EmbeddingClient? embedding;
        EmbeddingModelDefinition? model;
        lock (_gate)
        {
            (embedding, model) = (_embedding, _open);
            if (embedding is null || model is null || _state is not ("ready" or "indexing")) return [];
            // The last worker ended while idle: a worker starts and loads the model in the background, and this query
            // is answered by words at once rather than after that wait.
            if (Cold)
            {
                if (_warmup.IsCompleted) _warmup = WarmAsync(embedding, model);
                return [];
            }
        }

        try
        {
            var vector = await embedding.EmbedQueryAsync(model.Query(query, codeQuery), cancellationToken).ConfigureAwait(false);
            return await _vectors.SearchAsync(vector, scope, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbeddingException exception)
        {
            _log?.Invoke($"Search by meaning could not embed a query: {exception.Message}");
            return [];
        }
    }

    // Under the gate: the client has no worker, or one is starting for queries.
    private bool Cold => _embedding is { } embedding && (!embedding.Running || !_warmup.IsCompleted);

    private async Task WarmAsync(EmbeddingClient embedding, EmbeddingModelDefinition model)
    {
        await Task.Yield();
        try
        {
            await embedding.EmbedQueryAsync(model.Query("search", false), _lifetime.Token).ConfigureAwait(false);
        }
        catch (EmbeddingException exception)
        {
            _log?.Invoke($"Search by meaning could not start its worker for queries: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            // Search by meaning ends.
        }
        catch (ObjectDisposedException)
        {
            // The client was replaced (another model, turned off).
        }

        Notify();
    }

    private void OnSettingsChanged(SearchSettings before, SearchSettings after)
    {
        // The batches at once change during the indexing, without giving up what runs.
        if (before.Parallelism != after.Parallelism) ApplyParallelism(after);
        if (before.Semantic == after.Semantic && string.Equals(before.Model, after.Model, StringComparison.Ordinal) && before.Enabled == after.Enabled) return;
        // The step under way (a batch, a download) is given up for what the settings say now.
        lock (_gate)
        {
            try { _step?.Cancel(); }
            catch (ObjectDisposedException) { /* The step had ended. */ }
        }

        Wake();
    }

    private void Wake() => _wake.Release();

    private void ApplyParallelism(SearchSettings settings)
    {
        EmbeddingClient? embedding;
        lock (_gate) embedding = _embedding;
        if (embedding is null) return;
        var parallelism = settings.EffectiveParallelism(Environment.ProcessorCount);
        embedding.SetParallelism(parallelism, EmbeddingClient.ThreadsPerWorker(parallelism, Environment.ProcessorCount));
    }

    private void Notify() => Changed?.Invoke();

    private void ApplyPause()
    {
        bool paused;
        EmbeddingClient? embedding;
        lock (_gate) (paused, embedding) = (_paused || _dictating, _embedding);
        if (embedding is not null) embedding.Paused = paused;
        if (!paused) Wake();
    }

    private void SetState(string state, string? error = null)
    {
        lock (_gate)
        {
            if (_state == state && _error == error) return;
            (_state, _error) = (state, error);
        }

        Notify();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate) _step = step;
            try
            {
                await StepAsync(step.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // The settings changed during the step: the next one follows them at once.
                continue;
            }
            catch (EmbeddingException exception) when (exception.Code == "closed")
            {
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
            {
                _log?.Invoke($"Search by meaning stopped: {exception.Message}");
                SetState("failed", exception.Message);
            }

            try { await _wake.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One step: follow the settings (start or stop the worker, download), then embed what is missing, batch by batch,
    // until nothing is left or a signal asks for something else.
    private async Task StepAsync(CancellationToken cancellationToken)
    {
        var settings = _host.GetSettings();
        var model = EmbeddingModelCatalog.Find(settings.Model) ?? EmbeddingModelCatalog.Presets[0];
        if (!settings.Enabled || !settings.Semantic)
        {
            await CloseClientAsync().ConfigureAwait(false);
            SetState("off");
            return;
        }

        if (!_installer.Supported)
        {
            SetState("failed", "unsupported_platform");
            return;
        }

        if (!_installer.IsInstalled(model) || _installer.RuntimeLibrary() is null)
        {
            SetState("downloading");
            await _installer.InstallAsync(model).ConfigureAwait(false);
            if (_installer.Current.State != "done")
            {
                SetState(_installer.Current.State == "canceled" ? "off" : "failed", _installer.Current.Error);
                return;
            }
        }

        if (_open?.Id != model.Id || _embedding is null)
        {
            await CloseClientAsync().ConfigureAwait(false);
            SetState("loading");
            await _vectors.OpenAsync(model, SherpaRuntimeVersion, cancellationToken).ConfigureAwait(false);
            var parallelism = settings.EffectiveParallelism(Environment.ProcessorCount);
            var load = new EmbeddingWorkerLoad(model.Id, _installer.DirectoryOf(model), _installer.RuntimeLibrary()!, EmbeddingClient.ThreadsPerWorker(parallelism, Environment.ProcessorCount));
            var embedding = _client(load);
            lock (_gate) (_embedding, _open) = (embedding, model);
            ApplyParallelism(settings);
            ApplyPause();
        }

        await _vectors.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await EmbedMissingAsync(model, cancellationToken).ConfigureAwait(false);
    }

    // Embeds what is missing, a window at a time: twice as many batches as run at once, so that a worker that is done
    // finds the next batch waiting. The window is sorted by length, so that a batch pads its texts little.
    private async Task EmbedMissingAsync(EmbeddingModelDefinition model, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _host.GetSettings();
            if (!settings.Semantic || !string.Equals(settings.Model, model.Id, StringComparison.OrdinalIgnoreCase)) return;
            var parallelism = settings.EffectiveParallelism(Environment.ProcessorCount);
            var missing = await _vectors.MissingAsync(BatchSize * parallelism * 2, cancellationToken).ConfigureAwait(false);
            if (missing.Count == 0)
            {
                SetState("ready");
                return;
            }

            SetState("indexing");
            EmbeddingClient embedding;
            lock (_gate) embedding = _embedding!;
            var watch = Stopwatch.StartNew();
            var batches = missing.OrderBy(static chunk => chunk.Text.Length + (chunk.Title?.Length ?? 0)).Chunk(BatchSize).ToArray();
            var running = batches.Select(batch => (Batch: batch, Vectors: embedding.EmbedDocumentsAsync([.. batch.Select(chunk => model.Document(chunk.Title, chunk.Text))], cancellationToken))).ToList();
            var embedded = 0;
            try
            {
                while (running.Count > 0)
                {
                    await Task.WhenAny(running.Select(static entry => entry.Vectors)).ConfigureAwait(false);
                    foreach (var entry in running.Where(static entry => entry.Vectors.IsCompleted).ToArray())
                    {
                        running.Remove(entry);
                        float[][] vectors;
                        try
                        {
                            vectors = await entry.Vectors.ConfigureAwait(false);
                        }
                        catch (EmbeddingException exception) when (exception.Code != "closed")
                        {
                            // A failed batch is tried once more (the next window takes its chunks again); then search by
                            // meaning says it failed and keyword search goes on.
                            var failures = Interlocked.Increment(ref _failures);
                            _log?.Invoke($"Search by meaning could not embed a batch: {exception.Message}");
                            if (failures >= 2 && exception.Code == "engine_failed")
                            {
                                // Texts the model refuses get an empty vector: they are found by their words, and never block the others.
                                Interlocked.Exchange(ref _failures, 0);
                                await _vectors.PutAsync([.. entry.Batch.Select(chunk => (chunk, new float[model.Dimensions]))], cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            if (failures >= 2)
                            {
                                await CloseClientAsync().ConfigureAwait(false);
                                SetState("failed", exception.Code);
                                return;
                            }

                            continue;
                        }

                        Interlocked.Exchange(ref _failures, 0);
                        await _vectors.PutAsync([.. entry.Batch.Zip(vectors)], cancellationToken).ConfigureAwait(false);
                        embedded += entry.Batch.Length;
                        var rate = embedded / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                        lock (_gate) _rate = _rate <= 0 ? rate : _rate * 0.7 + rate * 0.3;
                        Notify();
                    }
                }
            }
            finally
            {
                // The batches still under way when the loop stops (a failure, other settings) end on their own.
                foreach (var entry in running) _ = entry.Vectors.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    /// <summary>The version of the native runtime the vectors are made with: vectors of another runtime differ a little.</summary>
    internal const string SherpaRuntimeVersion = "onnxruntime-1.28.2/sherpa-onnx-" + Speech.SpeechModelCatalog.EngineVersion;

    private async Task CloseClientAsync()
    {
        EmbeddingClient? embedding;
        lock (_gate)
        {
            embedding = _embedding;
            _embedding = null;
        }

        if (embedding is not null) await embedding.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _indexer.Changed -= Wake;
        _host.SettingsChanged -= OnSettingsChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await CloseClientAsync().ConfigureAwait(false);
        await _installer.CloseAsync().ConfigureAwait(false);
        await _vectors.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
