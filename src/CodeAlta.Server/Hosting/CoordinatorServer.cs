using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using CodeAlta.Server.Storage.Sqlite;
using CodeAlta.Server.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Hosting;

/// <summary>The options of a <see cref="CoordinatorServer"/>.</summary>
public sealed class CoordinatorServerOptions
{
    /// <summary>Gets the data root: the database, the setup code, the lock and the state of the running server.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Gets the addresses to listen on; <c>http://127.0.0.1:2586</c> when empty.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>Gets a certificate file (PKCS #12) for the <c>https://</c> addresses, or <see langword="null"/>.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Gets the password of <see cref="CertificatePath"/>, or <see langword="null"/>.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>Gets how long a setup code works.</summary>
    public TimeSpan SetupCodeLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets how far the time of a signed request may be from the server's.</summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the limits of the control connections.</summary>
    public FleetConnectionPolicy ConnectionPolicy { get; init; } = FleetConnectionPolicy.CreateDefault();

    /// <summary>Gets the limits of the relayed session channels.</summary>
    public RelayOptions Relay { get; init; } = new();

    /// <summary>Gets who may reach whom through the relay; null for two approved devices of the same organization.</summary>
    public IRelayAccessPolicy? RelayAccess { get; init; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets what configures the log, or <see langword="null"/> for the console.</summary>
    public Action<ILoggingBuilder>? ConfigureLogging { get; init; }

    /// <summary>
    /// Gets what gives the value of a variable of the environment, for the standard <c>OTEL_*</c> variables of the
    /// telemetry; the environment of the process by default (tests give their own).
    /// </summary>
    public Func<string, string?> EnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Gets how often the telemetry reads its metrics and sends its batches; null for the defaults of OpenTelemetry (60 s for metrics, 5 s for spans and logs).</summary>
    public TimeSpan? TelemetryExportInterval { get; init; }

    /// <summary>Gets the default address.</summary>
    public static string DefaultUrl => $"http://127.0.0.1:{ServerPaths.DefaultPort}";

    /// <summary>Gets what reads the versions of the server and its self-update (<c>GET /api/v1/server/update</c>); null for the running version alone.</summary>
    public Func<FleetUpdateStatus?>? UpdateStatus { get; init; }

    /// <summary>
    /// Gets what updates the server itself, last in a campaign of the fleet (its self-update's <c>ApplyAsync</c>, given the
    /// request and who asks); null for a server that does not update itself (its campaigns fail at the server's step).
    /// </summary>
    public Func<CodeAlta.Fleet.Update.UpdateRequest, string, CancellationToken, Task<CodeAlta.Fleet.Update.StagedUpdate?>>? ApplyUpdate { get; init; }

    /// <summary>
    /// Gets what withdraws the server's own switch that waits, only when a given requester asked for it (a campaign of the
    /// fleet that a new policy revokes during the server's step), given the requester and why; it gives whether that
    /// switch waited and was withdrawn, and throws <see cref="CodeAlta.Fleet.Update.UpdateException"/> when the state of
    /// the versions cannot be written. Null for none.
    /// </summary>
    public Func<string, string, bool>? CancelUpdate { get; init; }

    /// <summary>Gets the runtime of the server's package (<c>win-x64</c>); null for this process's.</summary>
    public string? UpdateRuntime { get; init; }

    /// <summary>Gets how often the update campaigns move on without an event.</summary>
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how many nodes update at once in a campaign.</summary>
    public int UpdateConcurrency { get; init; } = 2;

    /// <summary>Gets how long a campaign waits for a node that went offline while it updated.</summary>
    public TimeSpan UpdateOfflineGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets what reads the latest stable version of a feed for an automatic policy, for tests; null to read the feed.</summary>
    public Func<string, CancellationToken, Task<string?>>? ReadLatestUpdate { get; init; }
}

/// <summary>What a running server writes in its data root, for <c>altaserver status</c>.</summary>
/// <param name="ProcessId">The process of the server.</param>
/// <param name="Urls">The addresses it listens on.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Version">Its version.</param>
public sealed record ServerState(int ProcessId, IReadOnlyList<string> Urls, DateTimeOffset StartedAt, string Version);

/// <summary>
/// CodeAlta Server: one process with Kestrel, the REST API, the control connection, the store and the in-memory
/// presence. It owns its data root while it runs: a second server on the same data root does not start.
/// </summary>
/// <remarks>
/// Plain HTTP is for the loopback (and tests); a server reached from other machines listens with a certificate, or —
/// the usual way in production — stays on the loopback behind Tailscale Serve, Caddy or the TLS of its platform, which
/// terminates TLS in front of it. The device key signs every request either way.
/// </remarks>
public sealed class CoordinatorServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ICoordinatorStore _store;
    private readonly FileStream _lock;
    private readonly ServerPaths _paths;
    private readonly ServerTelemetry _telemetry;
    private int _disposed;

    private CoordinatorServer(WebApplication app, ICoordinatorStore store, FileStream dataRootLock, ServerPaths paths, IReadOnlyList<Uri> addresses, SetupCode? setupCode,
        ServerTelemetry telemetry)
    {
        _app = app;
        _store = store;
        _lock = dataRootLock;
        _paths = paths;
        _telemetry = telemetry;
        Addresses = addresses;
        SetupCode = setupCode;
    }

    /// <summary>Gets the addresses the server listens on (the actual ports when 0 was asked).</summary>
    public IReadOnlyList<Uri> Addresses { get; }

    /// <summary>Gets the setup code made at the start of a server without an owner, or <see langword="null"/>.</summary>
    public SetupCode? SetupCode { get; }

    /// <summary>Gets the files of the server.</summary>
    public ServerPaths Paths => _paths;

    /// <summary>Gets the store, for tests and tools of the same process.</summary>
    internal ICoordinatorStore Store => _store;

    /// <summary>Copies the database into a new file, while the server runs (before an update).</summary>
    /// <param name="destinationPath">The file to write; it must not exist.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes once the copy is written.</returns>
    /// <exception cref="NotSupportedException">The store is not the SQLite one.</exception>
    /// <exception cref="IOException">The destination exists.</exception>
    public Task BackupDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default)
        => _store is SqliteCoordinatorStore sqlite
            ? sqlite.BackupAsync(destinationPath, cancellationToken)
            : throw new NotSupportedException("Only the SQLite store is copied by the server.");

    /// <summary>Gets the services of the server.</summary>
    internal IServiceProvider Services => _app.Services;

    /// <summary>Gets the telemetry of the server.</summary>
    internal ServerTelemetry Telemetry => _telemetry;

    /// <summary>Starts a server.</summary>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The running server.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The data root is not absolute, or an address is not valid.</exception>
    /// <exception cref="InvalidOperationException">Another server uses the data root, or the database is newer than this server.</exception>
    /// <exception cref="IOException">An address is in use, or the data root cannot be written.</exception>
    public static async Task<CoordinatorServer> StartAsync(CoordinatorServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var paths = new ServerPaths(options.DataRoot);
        Directory.CreateDirectory(paths.DataRoot);
        var dataRootLock = AcquireLock(paths);
        SqliteCoordinatorStore? store = null;
        ServerMetrics? metrics = null;
        ServerTelemetry? telemetry = null;
        try
        {
            store = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions
            {
                DatabasePath = paths.DatabasePath,
                TimeProvider = options.TimeProvider,
                ObserveOperation = (operation, duration, failed) => metrics?.StoreOperation(operation, duration, failed),
            });
            var created = store;
            metrics = new ServerMetrics(ServerVersion.Current, () => created.WriteQueueDepth);
            telemetry = new ServerTelemetry(paths, metrics, options.EnvironmentVariable, options.TelemetryExportInterval);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var time = options.TimeProvider;
            var codes = new SetupCodeStore(paths.SetupCodePath, time);
            var organization = await store.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
            SetupCode? setupCode = null;
            if (organization is null) setupCode = codes.GetOrCreate(options.SetupCodeLifetime);
            else codes.Consume();

            var app = Build(options, store, codes, telemetry);
            try
            {
                telemetry.SetLog(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Telemetry"));
                // The fleet's setting applies from the start; the server's own file and environment win over it.
                telemetry.Apply(organization is null ? null : await store.GetTelemetrySettingsAsync(organization.Id, cancellationToken).ConfigureAwait(false),
                    organization?.Id);
                await LogIntegrityAsync(app, store, organization, cancellationToken).ConfigureAwait(false);
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                // The campaigns a stop interrupted go on, the server's own step included (a new version confirms it).
                app.Services.GetRequiredService<UpdateCampaignService>().Start();
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(static address => new Uri(address)).ToArray();
            var server = new CoordinatorServer(app, store, dataRootLock, paths, addresses, setupCode, telemetry);
            server.WriteState(time.GetUtcNow());
            return server;
        }
        catch
        {
            telemetry?.Dispose();
            metrics?.Dispose();
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            await dataRootLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Waits until the server is stopped (Ctrl+C, SIGTERM) or <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>A task that completes when the server should stop.</returns>
    public async Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.ApplicationStopping, cancellationToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is the end of the wait.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _app.Services.GetRequiredService<UpdateCampaignService>().DisposeAsync().ConfigureAwait(false);
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            // The last spans, metrics and records of the server go before it ends.
            _telemetry.Dispose();
            _telemetry.Metrics.Dispose();
            await _store.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Delete(_paths.StatePath);
            }
            catch (IOException)
            {
                // A reader holds it for an instant; the lock tells status that the server is gone.
            }

            await _lock.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads the state a running server wrote, if any.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <returns>The state, or <see langword="null"/>.</returns>
    public static ServerState? ReadState(ServerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            using var stream = new FileStream(paths.StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, ServerStateJsonContext.Default.ServerState);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Gets whether a server holds the lock of a data root now.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <returns>Whether the data root is in use.</returns>
    public static bool IsRunning(ServerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.LockPath)) return false;
        try
        {
            using var probe = new FileStream(paths.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static WebApplication Build(CoordinatorServerOptions options, SqliteCoordinatorStore store, SetupCodeStore codes, ServerTelemetry telemetry)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
        if (options.ConfigureLogging is not null)
        {
            builder.Logging.ClearProviders();
            options.ConfigureLogging(builder.Logging);
        }
        else
        {
            // The start and the stop, not a line per request: a request log would name every device's calls.
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        }

        // The records of the log also go to the collector, when the telemetry sends logs.
        builder.Logging.AddProvider(telemetry.Runtime);

        var urls = options.Urls.Count == 0 ? [CoordinatorServerOptions.DefaultUrl] : options.Urls;
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException($"'{url}' is not an http:// or https:// address.", nameof(options));
            }
        }

        if (urls.Any(static url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrEmpty(options.CertificatePath)) throw new ArgumentException("An https:// address needs a certificate (--certificate).", nameof(options));
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
            builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        }

        builder.WebHost.UseUrls([.. urls]);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = Math.Max(DeviceAuthenticationMiddleware.MaxBodyBytes, options.ConnectionPolicy.MaxFrameBytes);
        });

        var time = options.TimeProvider;
        var services = builder.Services;
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(1, FleetServerJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(2, FleetDeviceListJsonContext.Default);
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        services.AddProblemDetails();
        services.AddOpenApi(openApi => openApi.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "CodeAlta Server";
            document.Info.Version = "v1";
            document.Info.Description = "The REST API of CodeAlta Server. Every call except setup, enrollment and health is signed with a device key: "
                + $"{FleetSignedRequest.DeviceHeader}, {FleetSignedRequest.TimestampHeader}, {FleetSignedRequest.NonceHeader} and {FleetSignedRequest.SignatureHeader}.";
            return Task.CompletedTask;
        }));
        var metrics = telemetry.Metrics;
        services.AddSingleton(time);
        services.AddSingleton(new ServerUpdateStatusSource(options.UpdateStatus));
        services.AddSingleton<ICoordinatorStore>(store);
        services.AddSingleton(codes);
        services.AddSingleton(telemetry);
        services.AddSingleton(metrics);
        services.AddSingleton(new NonceCache(time, options.MaxClockSkew * 2 + TimeSpan.FromMinutes(1)));
        services.AddSingleton(provider => new RequestAuthenticator(store, provider.GetRequiredService<NonceCache>(), time, options.MaxClockSkew) { Metrics = metrics });
        services.AddSingleton<PresenceRegistry>();
        services.AddSingleton(_ => new DirectoryHub { Metrics = metrics });
        services.AddSingleton(provider => new SetupService(store, codes, provider.GetRequiredService<RequestAuthenticator>(), time));
        services.AddSingleton(provider => new RelayHub(store, provider.GetRequiredService<PresenceRegistry>(), provider.GetRequiredService<RequestAuthenticator>(),
            options.RelayAccess ?? new OrganizationRelayAccess(), options.Relay, time, provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Relay"))
        {
            Metrics = metrics,
            Stopping = provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
        });
        services.AddSingleton(provider => new DeviceService(store, provider.GetRequiredService<RequestAuthenticator>(), provider.GetRequiredService<PresenceRegistry>(), time,
            provider.GetRequiredService<RelayHub>()) { Metrics = metrics });
        services.AddSingleton(provider => new PublicationService(store, provider.GetRequiredService<DirectoryHub>(), time) { Metrics = metrics });
        services.AddSingleton(_ => new MetadataKeyService(store, time));
        services.AddSingleton(provider => new TelemetrySettingsService(store, provider.GetRequiredService<PresenceRegistry>(), telemetry, time,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Telemetry")));
        services.AddSingleton(provider => new OtlpForwarder(telemetry, provider.GetRequiredService<RequestAuthenticator>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Telemetry")));
        services.AddSingleton(provider => new UpdateCampaignService(store, provider.GetRequiredService<PresenceRegistry>(), provider.GetRequiredService<DirectoryHub>(),
            provider.GetRequiredService<ServerUpdateStatusSource>(), time, provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Update"),
            new UpdateCampaignOptions
            {
                Interval = options.UpdateInterval, ApplyServerUpdate = options.ApplyUpdate, WithdrawServerSwitch = options.CancelUpdate, ServerRuntime = options.UpdateRuntime,
                ReadLatest = options.ReadLatestUpdate,
                Limits = new UpdateCampaignLimits { MaxConcurrent = Math.Max(1, options.UpdateConcurrency), OfflineGrace = options.UpdateOfflineGrace },
            }));
        services.AddSingleton(provider => new ControlService(store, provider.GetRequiredService<PresenceRegistry>(), provider.GetRequiredService<DirectoryHub>(),
            provider.GetRequiredService<PublicationService>(), provider.GetRequiredService<RelayHub>(), time, options.ConnectionPolicy,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Control"))
        {
            Metrics = metrics,
            TelemetrySettings = provider.GetRequiredService<TelemetrySettingsService>(),
            Updates = provider.GetRequiredService<UpdateCampaignService>(),
            Stopping = provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
        });

        var app = builder.Build();
        app.UseMiddleware<HttpSpanEnricher>();
        app.UseMiddleware<ProblemDetailsMiddleware>();
        app.UseStatusCodePages();
        app.UseWebSockets();
        app.UseRouting();
        app.UseMiddleware<DeviceAuthenticationMiddleware>();
        app.MapOpenApi();
        ApiEndpoints.Map(app);
        return app;
    }

    private static async Task LogIntegrityAsync(WebApplication app, ICoordinatorStore store, OrganizationRecord? organization, CancellationToken cancellationToken)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server");
        var audit = await store.VerifyAuditAsync(cancellationToken).ConfigureAwait(false);
        if (!audit.IsValid) logger.LogError("The audit log does not verify: its chain breaks at record {Sequence}.", audit.BrokenAt);
        if (organization is null) return;
        var list = DeviceListChain.Verify(await store.ListDeviceListAsync(organization.Id, cancellationToken).ConfigureAwait(false));
        if (!list.IsValid) logger.LogError("The device list does not verify: it breaks at entry {Sequence}.", list.BrokenAt);
    }

    private static FileStream AcquireLock(ServerPaths paths)
    {
        try
        {
            return new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new DataRootInUseException($"Another CodeAlta Server uses the data root '{paths.DataRoot}'.", exception);
        }
    }

    private void WriteState(DateTimeOffset startedAt)
    {
        var state = new ServerState(Environment.ProcessId, Addresses.Select(static address => address.ToString().TrimEnd('/')).ToArray(), startedAt, ServerVersion.Current);
        var temporary = _paths.StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, ServerStateJsonContext.Default.ServerState));
        File.Move(temporary, _paths.StatePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ServerState))]
internal sealed partial class ServerStateJsonContext : JsonSerializerContext;
