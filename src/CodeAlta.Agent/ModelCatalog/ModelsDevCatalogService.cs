using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using XenoAtom.Logging;

namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// Maintains the current models.dev catalog snapshot for local model metadata enrichment.
/// </summary>
public sealed class ModelsDevCatalogService : IAsyncDisposable
{
    /// <summary>
    /// The snapshot content file name shipped with <c>CodeAlta.Agent</c>.
    /// </summary>
    public const string DefaultSnapshotFileName = "Data/models_dev_db.json";

    /// <summary>
    /// The legacy embedded snapshot resource name used by older builds.
    /// </summary>
    public const string DefaultSnapshotResourceName = "CodeAlta.Agent.Data.models_dev_db.json";

    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.ModelCatalog");

    private readonly object _gate = new();
    private readonly string _snapshotFilePath;
    private readonly string _snapshotResourceName;
    private readonly string? _cacheFilePath;
    private readonly Uri _refreshUri;
    private readonly TimeSpan _refreshInterval;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _backgroundRefreshTask;
    private readonly Lazy<Task> _disposeTask;
    private bool _stopping;
    private volatile ModelsDevDatabase _currentDatabase;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelsDevCatalogService"/> class.
    /// </summary>
    /// <param name="options">The catalog options.</param>
    public ModelsDevCatalogService(ModelsDevCatalogServiceOptions? options = null)
        : this(initialDatabase: null, options)
    {
    }

    internal ModelsDevCatalogService(ModelsDevDatabase? initialDatabase, ModelsDevCatalogServiceOptions? options)
    {
        _disposeTask = new Lazy<Task>(DisposeCoreAsync);
        options ??= new ModelsDevCatalogServiceOptions();
        _snapshotFilePath = string.IsNullOrWhiteSpace(options.SnapshotFilePath)
            ? Path.Combine(AppContext.BaseDirectory, DefaultSnapshotFileName)
            : Path.GetFullPath(options.SnapshotFilePath);
        _snapshotResourceName = string.IsNullOrWhiteSpace(options.SnapshotResourceName)
            ? DefaultSnapshotResourceName
            : options.SnapshotResourceName.Trim();
        _cacheFilePath = string.IsNullOrWhiteSpace(options.CacheFilePath)
            ? null
            : Path.GetFullPath(options.CacheFilePath);
        _refreshUri = options.RefreshUri ?? new Uri("https://models.dev/api.json", UriKind.Absolute);
        _refreshInterval = options.RefreshInterval <= TimeSpan.Zero
            ? TimeSpan.FromHours(12)
            : options.RefreshInterval;
        _httpClient = options.HttpClient ?? new HttpClient();
        _ownsHttpClient = options.HttpClient is null;
        _currentDatabase = initialDatabase ?? LoadInitialDatabase();
    }

    /// <summary>
    /// Gets the current in-memory database snapshot.
    /// </summary>
    public ModelsDevDatabase CurrentDatabase => _currentDatabase;

    /// <summary>
    /// Starts the non-blocking background refresh loop.
    /// </summary>
    /// <remarks>
    /// Before disposal starts, repeated calls do nothing once a refresh task has been retained,
    /// even if that task has completed. Calls after the stop decision are rejected.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The catalog has stopped admitting background refreshes.</exception>
    public void StartBackgroundRefresh()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_backgroundRefreshTask is not null)
            {
                return;
            }

            var token = _disposeCts.Token;
            _backgroundRefreshTask = Task.Run(() => RefreshLoopAsync(token));
        }
    }

    /// <summary>
    /// Tries to resolve a provider by models.dev provider identifier.
    /// </summary>
    /// <param name="providerId">The models.dev provider identifier.</param>
    /// <param name="provider">The resolved provider.</param>
    /// <returns><see langword="true"/> when found; otherwise <see langword="false"/>.</returns>
    public bool TryGetProvider(string providerId, [NotNullWhen(true)] out ModelsDevProviderDefinition? provider)
        => _currentDatabase.TryGetProvider(providerId, out provider);

    /// <summary>
    /// Tries to resolve a model by models.dev provider identifier and model identifier.
    /// </summary>
    /// <param name="providerId">The models.dev provider identifier.</param>
    /// <param name="modelId">The model identifier.</param>
    /// <param name="model">The resolved model.</param>
    /// <returns><see langword="true"/> when found; otherwise <see langword="false"/>.</returns>
    public bool TryGetModel(
        string providerId,
        string modelId,
        [NotNullWhen(true)] out ModelsDevModelDefinition? model)
        => _currentDatabase.TryGetModel(providerId, modelId, out model);

    /// <summary>
    /// Stops refresh admission, requests cancellation, joins the selected refresh task, and releases owned resources.
    /// </summary>
    /// <returns>The shared cleanup outcome; later calls do not retry failed stages.</returns>
    /// <remarks>
    /// One cleanup operation captures the retained refresh task under the start gate before requesting cancellation.
    /// An OperationCanceledException from joining that task is suppressed regardless of its task state.
    /// Other cancellation or join failures are retained; after the selected task terminates, source release and
    /// owned HTTP client release are attempted independently. A pending selected task prevents both releases.
    /// Failures retain cancellation, join, source, then owned-client order; one is rethrown and multiple failures
    /// are aggregated without flattening. Borrowed clients are not released, and the database remains queryable.
    /// This does not guarantee external dependency termination, constructor rollback, or recursive disposal safety.
    /// </remarks>
    /// <exception cref="Exception">A single non-cancellation cleanup failure is rethrown.</exception>
    /// <exception cref="OperationCanceledException">The only retained cleanup failure is cancellation.</exception>
    /// <exception cref="AggregateException">Multiple cleanup stages failed.</exception>
    public ValueTask DisposeAsync() => new(_disposeTask.Value);

    private async Task DisposeCoreAsync()
    {
        Task? original;
        lock (_gate)
        {
            _stopping = true;
            original = _backgroundRefreshTask;
        }

        await ModelsDevCatalogLifetime.DisposeRefreshAsync(
            original,
            _disposeCts.Cancel,
            _disposeCts.Dispose,
            _httpClient.Dispose,
            _ownsHttpClient).ConfigureAwait(false);
    }

    private ModelsDevDatabase LoadInitialDatabase()
    {
        var snapshot = LoadSnapshotDatabase();
        var cached = TryLoadCachedDatabase();
        return cached ?? snapshot;
    }

    private ModelsDevDatabase LoadSnapshotDatabase()
    {
        if (File.Exists(_snapshotFilePath))
        {
            try
            {
                using var stream = File.OpenRead(_snapshotFilePath);
                return ModelsDevDatabaseJson.Deserialize(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Logger.Warn($"Failed to load models.dev snapshot content file '{_snapshotFilePath}': {ex.Message}");
            }
        }

        return LoadEmbeddedDatabaseFallback();
    }

    private ModelsDevDatabase LoadEmbeddedDatabaseFallback()
    {
        var assembly = typeof(ModelsDevCatalogService).Assembly;
        using var stream = assembly.GetManifestResourceStream(_snapshotResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The models.dev snapshot content file '{_snapshotFilePath}' and legacy embedded resource '{_snapshotResourceName}' were not found.");
        }

        return ModelsDevDatabaseJson.Deserialize(stream);
    }

    private ModelsDevDatabase? TryLoadCachedDatabase()
    {
        if (string.IsNullOrWhiteSpace(_cacheFilePath) || !File.Exists(_cacheFilePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(_cacheFilePath);
            return ModelsDevDatabaseJson.Deserialize(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Logger.Warn($"Failed to load cached models.dev database '{_cacheFilePath}': {ex.Message}");
            return null;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_refreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await _httpClient.GetStreamAsync(_refreshUri, cancellationToken).ConfigureAwait(false);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            memory.Position = 0;

            var database = ModelsDevDatabaseJson.Deserialize(memory);
            _currentDatabase = database;
            await PersistCacheAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Logger.Warn($"Failed to refresh models.dev catalog from '{_refreshUri.ToString()}': {ex.Message}");
        }
    }

    private async Task PersistCacheAsync(ModelsDevDatabase database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cacheFilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_cacheFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = ModelsDevDatabaseJson.SerializeUtf8(database);
        var tempPath = $"{_cacheFilePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }

            throw;
        }
    }
}

/// <summary>
/// Describes how the models.dev catalog service loads and refreshes snapshots.
/// </summary>
public sealed class ModelsDevCatalogServiceOptions
{
    /// <summary>
    /// Gets or sets the snapshot content-file path.
    /// </summary>
    public string? SnapshotFilePath { get; init; }

    /// <summary>
    /// Gets or sets the embedded snapshot resource name.
    /// </summary>
    public string? SnapshotResourceName { get; init; }

    /// <summary>
    /// Gets or sets the optional cache-file path.
    /// </summary>
    public string? CacheFilePath { get; init; }

    /// <summary>
    /// Gets or sets the refresh endpoint.
    /// </summary>
    public Uri? RefreshUri { get; init; }

    /// <summary>
    /// Gets or sets the refresh interval.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Gets or sets the HTTP client used for background refreshes.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>
/// Performs the catalog's selected-refresh cleanup using supplied operations.
/// </summary>
internal static class ModelsDevCatalogLifetime
{
    /// <summary>
    /// Requests cancellation, joins the supplied original, then attempts eligible resource releases.
    /// </summary>
    /// <param name="original">The actual retained refresh task, or null when refresh was never started.</param>
    /// <param name="cancel">The mandatory cancellation operation.</param>
    /// <param name="releaseSource">The mandatory source-release operation.</param>
    /// <param name="releaseHttpClient">The mandatory HTTP client-release operation, including when borrowed.</param>
    /// <param name="ownsHttpClient">Whether to invoke the HTTP client-release operation.</param>
    /// <returns>The cleanup task, started inline after synchronous callback validation.</returns>
    /// <remarks>
    /// Validates callbacks in parameter order and allocates storage for at most four direct failures before callbacks.
    /// Only OperationCanceledException from the original join is suppressed, including from a faulted original.
    /// Callback failures, including cancellation, are retained. A pending original prevents resource release even
    /// after cancellation failure. After terminal join, source and owned-client releases are attempted independently.
    /// Failures are reported in cancellation, original, source, then owned-client order: one via exception dispatch
    /// information, multiple via a direct aggregate without flattening or deduplication. A lone callback cancellation
    /// can produce a canceled cleanup task. This operation does not bound synchronous callbacks or pending work,
    /// recover allocation/setup failures, establish external dependency termination, or support recursive disposal.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory callback is null; validation throws synchronously.</exception>
    /// <exception cref="Exception">A single non-cancellation cleanup failure is rethrown by the returned task.</exception>
    /// <exception cref="OperationCanceledException">The only retained cleanup failure is callback cancellation.</exception>
    /// <exception cref="AggregateException">Multiple cleanup stages failed.</exception>
    internal static Task DisposeRefreshAsync(
        Task? original,
        Action cancel,
        Action releaseSource,
        Action releaseHttpClient,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(releaseSource);
        ArgumentNullException.ThrowIfNull(releaseHttpClient);

        return DisposeCoreAsync();

        async Task DisposeCoreAsync()
        {
            var failures = new List<Exception>(4);
            try
            {
                cancel();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (original is not null)
            {
                try
                {
                    await original.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                releaseSource();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (ownsHttpClient)
            {
                try
                {
                    releaseHttpClient();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }
        }
    }
}
