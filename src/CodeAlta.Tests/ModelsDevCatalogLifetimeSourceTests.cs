using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Source-only gates for the accepted models.dev selected-refresh lifetime contract.</summary>
/// <remarks>
/// No production type or runtime core is invoked. Admission, Lazy sharing and cancellation ordering
/// are source-wiring obligations, not actual service concurrency or installed-BCL evidence.
/// Fixed read map: the first four methods each read only the catalog once; the fifth reads ten
/// other named files once each. A complete traversal has 14 reads of 11 unique paths; failures
/// can stop earlier. ReadSource is the sole filesystem operation, with one File.ReadAllBytes
/// per call and a deterministic CallerFilePath-derived ../ root, without discovery or probes.
/// All other helpers operate only on supplied text/bytes. No assembly loading, production calls,
/// concrete catalog, CTS, client, timer, owner, UI, logger, config, home or artifact access occurs.
/// Reads are nonzero I/O; later test-assembly logging is not initialization-free qualification.
/// LF literals explicitly reconstruct CRLF catalog text, including a final newline. Complete
/// original and E1-E6 XML/member anchors are frozen; inverses require each entire NEW literal.
/// Missing future contracts must fail, not silently accept the unchanged original catalog.
/// </remarks>
[TestClass]
public sealed class ModelsDevCatalogLifetimeSourceTests
{
    [TestMethod]
    public void Start_SourceWiring_StopsAdmissionBeforeTokenCaptureAndScheduling()
    {
        var source = ReadSource(Catalog);
        RequireOnce(source.Text, CatalogLiteral(NewStart));
        RequireOnce(source.Text, "Task.Run(");
        AssertAcceptedCatalog(source);
    }

    [TestMethod]
    public void Dispose_SourceWiring_SharesOneStopSnapshotAndCancellation()
    {
        var source = ReadSource(Catalog);
        RequireOnce(source.Text, CatalogLiteral(NewFields));
        RequireOnce(source.Text, CatalogLiteral(NewConstructor));
        RequireOnce(source.Text, CatalogLiteral(NewDisposal));
        RequireOnce(source.Text, "_disposeTask = new Lazy<Task>(DisposeCoreAsync);");
        RequireOnce(source.Text, "public ValueTask DisposeAsync() => new(_disposeTask.Value);");
        AssertAcceptedCatalog(source);
    }

    [TestMethod]
    public void Dispose_SourceWiring_UsesMandatoryLoggerFreeCore()
    {
        var source = ReadSource(Catalog);
        RequireOnce(source.Text, CatalogLiteral(NewDisposal));
        RequireOnce(source.Text, CatalogLiteral(NewOptionsAndLifetime));
        RequireOnce(source.Text, CatalogLiteral(NewImports));
        AssertAcceptedCatalog(source);
    }

    [TestMethod]
    public void Preservation_InvertsOnlyApprovedCatalogLifetimeChanges()
    {
        var source = ReadSource(Catalog);
        // Inversion first requires E6 NEW, even on the unchanged baseline. No old-code fallback.
        AssertAcceptedCatalog(source);
    }

    [TestMethod]
    public void Preservation_OwnershipAndExistingGuardsRemainUnchanged()
    {
        // Independent of the missing future catalog contract; every entry is a fixed source path.
        foreach (var baseline in FrozenRoutes())
        {
            var source = ReadSource(baseline);
            AssertBaseline(source, baseline);
        }
    }

    private readonly record struct Baseline(string Path, int Bytes, int Lines, bool CrLf, string Hash);
    private readonly record struct Source(string Text, byte[] Bytes);
    private readonly record struct Edit(string Before, string After);

    private static Baseline Catalog => new("CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs", 10673, 298, true,
        "4FF490A7A3D403D6E9D72D063D9A87B0A34348D24F1D98DB635F52E77C374A15");

    private static Baseline FutureCatalog => new("CodeAlta.Agent/ModelCatalog/ModelsDevCatalogService.cs", 16820, 417, true,
        "5293CB31630ED2ECABE9CAB719BDBECA3CDC9FFD2B9228382CD154F06F77ECAB");

    private static Baseline[] FrozenRoutes() =>
    [
        new("CodeAlta.Tui/App/CodeAltaOwnedServices.cs", 17484, 379, true,
            "D2AD29EEF926D2D5F3BF6E99367D2D4C67F9C14BC4855ECD508D1676D49CBE61"),
        new("CodeAlta.Tests/ModelsDevCatalogTests.cs", 24657, 636, true,
            "95FA97004A036B9EFAF0D1C978698E00C9F3484D0C87CAEA17B9047344A9CF51"),
        new("CodeAlta.Tests/CodeAltaOwnedServicesLifetimeTests.cs", 41906, 934, false,
            "78A7B045DD137F3ABC1EA5AB2A5222F33AD668300B2915C12F99DCD29065620C"),
        new("CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs", 28073, 503, false,
            "FF1F2C40CA27A4E0462AE034817A70986A74742CF4A379E4D0BB5ABC8C8A6793"),
        new("CodeAlta.Tests/ArchitectureGuardrailTests.cs", 147905, 2320, true,
            "027D6857C155708F2BE147B169E3C4F57B02AE53D3C6B04345350C2B88371B4D"),
        new("CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs", 54692, 1202, false,
            "F65FA0B9652FC4F2BE92C89386660BEEF3E4FC1933E65162D823FCAF94B292FD"),
        new("CodeAlta.Hosting/ConfiguredModelProviderRegistryBuilder.cs", 64999, 1384, true,
            "04AE668FEAE3C9D938856E02C7167AC2403FC1FC6FB2616A58E885404CE6C432"),
        new("CodeAlta.Hosting/ConfiguredProviderInspection.cs", 13770, 241, false,
            "3BD3881A8AF8178C708EAA695D5DB3212D0D206F1C984A4E5990F61E6791A8AC"),
        new("CodeAlta.Tui/App/ProviderFrontendCoordinator.cs", 30971, 682, true,
            "5A9E4705CF092BD2BCCAC1045BAD5C21671D785EA194D4FA7251812530DCE429"),
        new("CodeAlta.Agent/ModelCatalog/AgentModelMetadataEnricher.cs", 7595, 183, true,
            "6E5707B44DCDE30CC3925059D6789FF11380B74FC3F5F8C4524D83ED786D43A1"),
    ];

    // E1-E6 in accepted forward order. Every literal includes exactly one final CRLF via CatalogLiteral.
    private static Edit[] CatalogEdits() =>
    [
        new(CatalogLiteral(OldImports), CatalogLiteral(NewImports)),
        new(CatalogLiteral(OldFields), CatalogLiteral(NewFields)),
        new(CatalogLiteral(OldConstructor), CatalogLiteral(NewConstructor)),
        new(CatalogLiteral(OldStart), CatalogLiteral(NewStart)),
        new(CatalogLiteral(OldDisposal), CatalogLiteral(NewDisposal)),
        new(CatalogLiteral(OldOptions), CatalogLiteral(NewOptionsAndLifetime)),
    ];

    private const string OriginalCatalog = """
    using System.Diagnostics.CodeAnalysis;
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
        public void StartBackgroundRefresh()
        {
            lock (_gate)
            {
                if (_backgroundRefreshTask is not null)
                {
                    return;
                }

                _backgroundRefreshTask = Task.Run(() => RefreshLoopAsync(_disposeCts.Token));
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

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            _disposeCts.Cancel();

            Task? refreshTask;
            lock (_gate)
            {
                refreshTask = _backgroundRefreshTask;
            }

            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _disposeCts.Dispose();
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
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
    """;

    private const string OldImports = """
    using System.Diagnostics.CodeAnalysis;
    using XenoAtom.Logging;
    """;

    private const string NewImports = """
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.ExceptionServices;
    using XenoAtom.Logging;
    """;

    private const string OldFields = """
        private Task? _backgroundRefreshTask;
        private volatile ModelsDevDatabase _currentDatabase;
    """;

    private const string NewFields = """
        private Task? _backgroundRefreshTask;
        private readonly Lazy<Task> _disposeTask;
        private bool _stopping;
        private volatile ModelsDevDatabase _currentDatabase;
    """;

    private const string OldConstructor = """
        internal ModelsDevCatalogService(ModelsDevDatabase? initialDatabase, ModelsDevCatalogServiceOptions? options)
        {
            options ??= new ModelsDevCatalogServiceOptions();
    """;

    private const string NewConstructor = """
        internal ModelsDevCatalogService(ModelsDevDatabase? initialDatabase, ModelsDevCatalogServiceOptions? options)
        {
            _disposeTask = new Lazy<Task>(DisposeCoreAsync);
            options ??= new ModelsDevCatalogServiceOptions();
    """;

    private const string OldStart = """
        /// <summary>
        /// Starts the non-blocking background refresh loop.
        /// </summary>
        public void StartBackgroundRefresh()
        {
            lock (_gate)
            {
                if (_backgroundRefreshTask is not null)
                {
                    return;
                }

                _backgroundRefreshTask = Task.Run(() => RefreshLoopAsync(_disposeCts.Token));
            }
        }
    """;

    private const string NewStart = """
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
    """;

    private const string OldDisposal = """
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            _disposeCts.Cancel();

            Task? refreshTask;
            lock (_gate)
            {
                refreshTask = _backgroundRefreshTask;
            }

            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _disposeCts.Dispose();
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    """;

    private const string NewDisposal = """
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
    """;

    private const string OldOptions = """
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
    """;

    private const string NewOptionsAndLifetime = """
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
    """;

    // Helpers below never evaluate the C# held in the literals or load any production assembly.
    private static Source ReadSource(Baseline baseline, [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));
        var bytes = File.ReadAllBytes(Path.Combine(root, baseline.Path));
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var source = new Source(text, bytes);
        AssertEncoding(source, baseline);
        return source;
    }

    private static void AssertEncoding(Source source, Baseline baseline)
    {
        Assert.IsFalse(source.Text.StartsWith("\uFEFF", StringComparison.Ordinal), baseline.Path + ": BOM");
        Assert.IsTrue(source.Text.EndsWith("\n", StringComparison.Ordinal), baseline.Path + ": final newline");
        var lf = source.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.IsFalse(lf.Contains('\r'), baseline.Path + ": lone CR");
        var expected = baseline.CrLf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
        Assert.IsTrue(string.Equals(source.Text, expected, StringComparison.Ordinal), baseline.Path + ": exact line endings");
        CollectionAssert.AreEqual(Encode(expected), source.Bytes, baseline.Path + ": raw encoding round trip");
    }

    private static byte[] Encode(string text) => new UTF8Encoding(false, true).GetBytes(text);

    private static string CatalogLiteral(string literal)
    {
        Assert.IsFalse(literal.Contains('\r'), "Frozen fixture literals must use LF.");
        Assert.IsFalse(literal.EndsWith("\n", StringComparison.Ordinal), "Raw literals exclude the final newline.");
        return literal.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
    }

    private static void AssertBaseline(Source source, Baseline baseline)
    {
        AssertEncoding(source, baseline);
        Assert.AreEqual(baseline.Bytes, source.Bytes.Length, baseline.Path + ": whole byte count");
        Assert.AreEqual(baseline.Lines, source.Text.Count(static character => character == '\n'), baseline.Path + ": line count");
        Assert.IsTrue(string.Equals(baseline.Hash, Convert.ToHexString(SHA256.HashData(source.Bytes)),
            StringComparison.Ordinal), baseline.Path + ": whole raw-byte hash");
    }

    private static void AssertAcceptedCatalog(Source source)
    {
        var edits = CatalogEdits();
        var restored = Invert(source.Text, edits);
        var original = CatalogLiteral(OriginalCatalog);
        Assert.IsTrue(string.Equals(original, restored, StringComparison.Ordinal), "Whole original catalog text");
        CollectionAssert.AreEqual(Encode(original), Encode(restored), "Every reconstructed original byte");
        AssertBaseline(new Source(restored, Encode(restored)), Catalog);

        var forward = Forward(original, edits);
        Assert.IsTrue(string.Equals(forward, source.Text, StringComparison.Ordinal), "Only the six approved catalog edits");
        CollectionAssert.AreEqual(Encode(forward), source.Bytes, "Every accepted future checkout byte");
        AssertBaseline(source, FutureCatalog);
    }

    private static string Forward(string original, Edit[] edits)
    {
        foreach (var edit in edits)
        {
            RequireOnce(original, edit.Before);
            original = original.Replace(edit.Before, edit.After, StringComparison.Ordinal);
        }

        return original;
    }

    private static string Invert(string future, Edit[] edits)
    {
        // Entire literal inverses E6 -> E1, including all XML and the complete E6 options anchor.
        // Requiring NEW before replacement prevents an unchanged-baseline preservation pass.
        for (var index = edits.Length - 1; index >= 0; index--)
        {
            var edit = edits[index];
            RequireOnce(future, edit.After);
            future = future.Replace(edit.After, edit.Before, StringComparison.Ordinal);
        }

        return future;
    }

    private static void RequireOnce(string source, string expected)
    {
        Assert.IsTrue(expected.Length > 0);
        var actual = 0;
        var offset = 0;
        while ((offset = source.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            actual++;
            offset += expected.Length;
        }

        Assert.AreEqual(1, actual, "Exact source occurrence count: " + expected);
    }
}
