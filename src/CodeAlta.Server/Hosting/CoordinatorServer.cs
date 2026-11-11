using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Api;
using CodeAlta.Server.Control;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Identity.SignIn;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using CodeAlta.Server.Storage.Sqlite;
using CodeAlta.Server.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// Gets whether an <c>http://</c> address beyond the loopback is allowed (<c>--allow-insecure-http</c>): off by default,
    /// so that a server reached from other machines has TLS, its own or that of what is put in front of it.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    /// Gets whether the <c>https://</c> addresses use a certificate the server makes once and keeps (<c>--own-certificate</c>),
    /// when <see cref="CertificatePath"/> names none: no system trusts it, its clients pin its key (<see cref="CoordinatorServer.CertificatePin"/>).
    /// </summary>
    public bool OwnCertificate { get; init; }

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

    /// <summary>
    /// Gets whether the update campaigns move on by themselves (on a timer and at each event, the default). Tests that step
    /// the campaigns by hand turn it off: a step of the loop beside theirs would race with their clock and their assertions.
    /// </summary>
    public bool UpdateLoop { get; init; } = true;

    /// <summary>Gets how many nodes update at once in a campaign.</summary>
    public int UpdateConcurrency { get; init; } = 2;

    /// <summary>Gets how long a campaign waits for a node that went offline while it updated.</summary>
    public TimeSpan UpdateOfflineGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets what reads the latest stable version of a feed for an automatic policy, for tests; null to read the feed.</summary>
    public Func<string, CancellationToken, Task<string?>>? ReadLatestUpdate { get; init; }

    /// <summary>
    /// Gets the sign-in of the server: the public clients of Microsoft, Google and GitHub and where their providers are;
    /// null for the built-in clients, overridden by the <c>CODEALTA_SIGNIN_*</c> variables of the environment.
    /// </summary>
    public SignInOptions? SignIn { get; init; }

    /// <summary>Gets the thresholds of the health of the machines: how long a problem lasts before its owner is told, how long a machine may be offline.</summary>
    public HealthOptions Health { get; init; } = new();

    /// <summary>Gets the limits of the requests of devices the server does not know yet: setup, sign-in, enrollment, recovery, the hooks' ingress.</summary>
    public AuthenticationRateLimits RateLimits { get; init; } = new();

    /// <summary>
    /// Gets the reverse proxies in front of the server whose <c>X-Forwarded-For</c> it believes (<c>--trusted-proxy</c>):
    /// addresses or networks; empty (the default) to count every request by its peer.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>Gets a fresh connect-only Dev Tunnel token for an approved device; null when the server uses no managed tunnel.</summary>
    public Func<CancellationToken, Task<FleetTunnelAccessDto?>>? TunnelAccess { get; init; }

    /// <summary>
    /// Gets settings added to the configuration of ASP.NET Core, as its environment variables (<c>Kestrel__Endpoints__...</c>)
    /// or an <c>appsettings.json</c> would give them; for tests of what the server does with them.
    /// </summary>
    internal IReadOnlyDictionary<string, string?>? HostConfiguration { get; init; }
}

/// <summary>What a running server writes in its data root, for <c>altaserver status</c>.</summary>
/// <param name="ProcessId">The process of the server.</param>
/// <param name="Urls">The addresses it listens on.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="Version">Its version.</param>
/// <param name="CertificatePin">The pin of the certificate of its <c>https://</c> addresses (<see cref="CodeAlta.Fleet.Protocol.FleetServerCertificates"/>), or null.</param>
public sealed record ServerState(int ProcessId, IReadOnlyList<string> Urls, DateTimeOffset StartedAt, string Version, string? CertificatePin = null);

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
        ServerTelemetry telemetry, string? certificatePin)
    {
        CertificatePin = certificatePin;
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

    /// <summary>
    /// Gets the pin of the certificate of the <c>https://</c> addresses (<see cref="CodeAlta.Fleet.Protocol.FleetServerCertificates"/>):
    /// what a client of a server with its own certificate is given beside the address; null without <c>https://</c>.
    /// </summary>
    public string? CertificatePin { get; }

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
        X509Certificate2? certificate = null;
        try
        {
            var sealer = ServerSecretSealer.Create(paths.KeysRoot);
            store = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions
            {
                DatabasePath = paths.DatabasePath,
                TimeProvider = options.TimeProvider,
                Sealer = sealer,
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

            var urls = options.Urls.Count == 0 ? [CoordinatorServerOptions.DefaultUrl] : options.Urls;
            ServerBindings.Require(urls, options.AllowInsecureHttp);
            certificate = Certificate(options, urls, paths, sealer, time);
            var app = Build(options, urls, store, codes, telemetry, certificate);
            try
            {
                telemetry.SetLog(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Telemetry"));
                // The fleet's setting applies from the start; the server's own file and environment win over it.
                telemetry.Apply(organization is null ? null : await store.GetTelemetrySettingsAsync(organization.Id, cancellationToken).ConfigureAwait(false),
                    organization?.Id);
                await LogIntegrityAsync(app, store, organization, cancellationToken).ConfigureAwait(false);
                // Access control joins the devices and the document before the first request: who may revoke whose devices, where the document stands.
                _ = app.Services.GetRequiredService<AccessDocumentService>();
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                // What Kestrel listens on, whatever set it, is held to the same rule as the addresses asked.
                try
                {
                    ServerBindings.Require(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses, options.AllowInsecureHttp);
                }
                catch (ArgumentException)
                {
                    await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                // The campaigns a stop interrupted go on, the server's own step included (a new version confirms it).
                app.Services.GetRequiredService<UpdateCampaignService>().Start();
                // Machines offline too long, or behind the version of the server, are looked for on a timer.
                app.Services.GetRequiredService<HealthService>().Start();
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(static address => new Uri(address)).ToArray();
            var server = new CoordinatorServer(app, store, dataRootLock, paths, addresses, setupCode, telemetry,
                certificate is null ? null : CodeAlta.Fleet.Protocol.FleetServerCertificates.Pin(certificate));
            server.WriteState(time.GetUtcNow());
            return server;
        }
        catch
        {
            certificate?.Dispose();
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
            await _app.Services.GetRequiredService<HealthService>().DisposeAsync().ConfigureAwait(false);
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

    // The certificate of the https:// addresses: the file the owner gave, or the one the server keeps of its own; none
    // without https://.
    private static X509Certificate2? Certificate(CoordinatorServerOptions options, IReadOnlyList<string> urls, ServerPaths paths, ServerSecretSealer sealer, TimeProvider time)
    {
        if (!urls.Any(static url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return null;
        if (!string.IsNullOrEmpty(options.CertificatePath)) return X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
        if (options.OwnCertificate) return ServerOwnCertificate.LoadOrCreate(paths, sealer, time);
        throw new ArgumentException("An https:// address needs a certificate: --certificate with a file, or --own-certificate for one the server makes and its devices pin.",
            nameof(options));
    }

    private static WebApplication Build(CoordinatorServerOptions options, IReadOnlyList<string> urls, SqliteCoordinatorStore store, SetupCodeStore codes, ServerTelemetry telemetry,
        X509Certificate2? certificate)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
        if (options.HostConfiguration is { } added) builder.Configuration.AddInMemoryCollection(added);
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

        if (certificate is not null)
        {
            builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        }

        builder.WebHost.UseUrls([.. urls]);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // The addresses checked above are the only ones: the endpoints of Kestrel's configuration (Kestrel__Endpoints__...
            // in the environment, an appsettings.json) would replace them unchecked, and be reloaded as they change.
            kestrel.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
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
            json.SerializerOptions.TypeInfoResolverChain.Insert(3, FleetStatisticsJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(4, FleetAutomationApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(5, FleetSecretsApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Add(FleetSignInJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Add(FleetBudgetsApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Add(FleetAccessApiJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Add(FleetAccessDocumentJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Add(FleetHookApiJsonContext.Default);
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
        services.AddSingleton(new ServerTunnelAccessSource(options.TunnelAccess));
        services.AddSingleton<ICoordinatorStore>(store);
        services.AddSingleton(codes);
        services.AddSingleton(telemetry);
        services.AddSingleton(metrics);
        services.AddSingleton(new NonceCache(time, options.MaxClockSkew * 2 + TimeSpan.FromMinutes(1)));
        services.AddSingleton(new AuthenticationRateLimiter(options.RateLimits, time));
        services.AddSingleton(new TrustedProxies(options.TrustedProxies));
        services.AddSingleton(provider => new RequestAuthenticator(store, provider.GetRequiredService<NonceCache>(), time, options.MaxClockSkew) { Metrics = metrics });
        services.AddSingleton<PresenceRegistry>();
        services.AddSingleton(new DirectoryVisibility(store, store, time));
        services.AddSingleton(provider => new DirectoryHub { Metrics = metrics, Visibility = provider.GetRequiredService<DirectoryVisibility>() });
        services.AddSingleton(provider => new SetupService(store, codes, provider.GetRequiredService<RequestAuthenticator>(), time));
        services.AddSingleton(provider => new RelayHub(store, provider.GetRequiredService<PresenceRegistry>(), provider.GetRequiredService<RequestAuthenticator>(),
            options.RelayAccess ?? new OrganizationRelayAccess(), options.Relay, time, provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Relay"))
        {
            Metrics = metrics,
            Stopping = provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
        });
        var signIn = options.SignIn ?? SignInOptions.FromEnvironment(options.EnvironmentVariable);
        services.AddSingleton(new SignInAttempts(time, signIn.MaxPendingAttempts));
        services.AddSingleton(provider => new SignInService(store, store, provider.GetRequiredService<RequestAuthenticator>(), provider.GetRequiredService<SignInAttempts>(),
            signIn, time) { Access = provider.GetRequiredService<AccessService>(), Proxies = provider.GetRequiredService<TrustedProxies>(),
            RateLimiter = provider.GetRequiredService<AuthenticationRateLimiter>() });
        services.AddSingleton(provider => new DeviceService(store, provider.GetRequiredService<RequestAuthenticator>(), provider.GetRequiredService<PresenceRegistry>(), time,
            provider.GetRequiredService<RelayHub>())
        {
            Metrics = metrics, SignInGrants = provider.GetRequiredService<SignInAttempts>(), SignInStore = store, RateLimiter = provider.GetRequiredService<AuthenticationRateLimiter>(),
            FirstWayInWindow = signIn.FirstWayInWindow,
            // A device approved or revoked: the computers of owners key their grants of sealed content again for the devices of the grantees.
            DevicesChanged = organization => provider.GetRequiredService<AccessService>().Changed(organization),
            Directory = provider.GetRequiredService<DirectoryHub>(),
        });
        services.AddSingleton(provider => new PublicationService(store, provider.GetRequiredService<DirectoryHub>(), time) { Metrics = metrics });
        // A machine given to another person answers to its owner record: its keys, values, environments, budgets and hooks follow it.
        services.AddSingleton(_ => new MetadataKeyService(store, time) { Access = store });
        services.AddSingleton(provider => new FleetSecretService(store, store, time) { Presence = provider.GetRequiredService<PresenceRegistry>(), Access = store });
        services.AddSingleton(provider => new NotificationService(store, store, time) { Presence = provider.GetRequiredService<PresenceRegistry>(), Access = store });
        services.AddSingleton(provider => new BudgetService(store, store, provider.GetRequiredService<NotificationService>(), time,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Budgets")) { Access = store });
        services.AddSingleton(provider => new StatisticsService(store, time) { Metrics = metrics, Published = provider.GetRequiredService<BudgetService>().EvaluateAsync });
        services.AddSingleton(provider => new HealthService(store, store, provider.GetRequiredService<NotificationService>(), provider.GetRequiredService<PresenceRegistry>(),
            provider.GetRequiredService<ServerUpdateStatusSource>(), time, provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Health"), options.Health));
        services.AddSingleton(provider => new TelemetrySettingsService(store, provider.GetRequiredService<PresenceRegistry>(), telemetry, time,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Telemetry")));
        services.AddSingleton(provider => new OtlpForwarder(telemetry, provider.GetRequiredService<RequestAuthenticator>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Telemetry")));
        services.AddSingleton(provider => new UpdateCampaignService(store, provider.GetRequiredService<PresenceRegistry>(), provider.GetRequiredService<DirectoryHub>(),
            provider.GetRequiredService<ServerUpdateStatusSource>(), time, provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Update"),
            new UpdateCampaignOptions
            {
                Interval = options.UpdateInterval, RunLoop = options.UpdateLoop, ApplyServerUpdate = options.ApplyUpdate, WithdrawServerSwitch = options.CancelUpdate, ServerRuntime = options.UpdateRuntime,
                ReadLatest = options.ReadLatestUpdate,
                Limits = new UpdateCampaignLimits { MaxConcurrent = Math.Max(1, options.UpdateConcurrency), OfflineGrace = options.UpdateOfflineGrace },
            }));
        services.AddSingleton(provider =>
        {
            var access = new AccessService(store, store, store, provider.GetRequiredService<NotificationService>(), time)
            {
                Visibility = provider.GetRequiredService<DirectoryVisibility>(), OnCall = store, Logins = store,
            };
            var devices = provider.GetRequiredService<DeviceService>();
            var presence = provider.GetRequiredService<PresenceRegistry>();
            var relays = provider.GetRequiredService<RelayHub>();
            // A suspended person's devices: pending ones refused, connections and relays closed; approved ones leave the list by the console's entries.
            access.EndDevice = async (device, origin, token) =>
            {
                if (device.Status == DeviceStatuses.Pending)
                    await store.RefusePendingDeviceAsync(device.Id, time.GetUtcNow(), origin.Audit("device.refuse", null, device.Id), token).ConfigureAwait(false);
                await presence.DisconnectAsync(device.Id, new FleetError(FleetErrorCodes.Revoked, null, "The person of this device is suspended.")).ConfigureAwait(false);
                await relays.CloseDeviceAsync(device.Id).ConfigureAwait(false);
            };
            devices.MayRevokeOther = (actor, device, token) => access.MayRevokeAsync(actor, device, token);
            return access;
        });
        services.AddSingleton(provider => new AccessDocumentService(store, store, provider.GetRequiredService<AccessService>(), time));
        services.AddSingleton(provider => new PolicyService(store, store, store, provider.GetRequiredService<NotificationService>(), time));
        services.AddSingleton(provider => new FleetAutomationService(store, store, provider.GetRequiredService<PresenceRegistry>(), time,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Automations"))
        {
            Access = store,
            // The approvals of an automation go to its owner, the members of the team that owns it, and who is granted them.
            Notifications = provider.GetRequiredService<NotificationService>(),
            ApprovalRecipients = (automation, run, token) => provider.GetRequiredService<AccessService>().ApproversOfAsync(automation.OrganizationId, automation.AutomationId,
                automation.OwnerPersonId, run?.Approvers, run?.OnCall, token),
            MayTakeOver = (automation, person, token) => provider.GetRequiredService<AccessService>().ApprovesAsync(automation.OrganizationId, automation.AutomationId,
                automation.OwnerPersonId, person, token),
        });
        services.AddSingleton(provider => new HookService(store, store, provider.GetRequiredService<PresenceRegistry>(), time,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Hooks")) { Access = store });
        services.AddSingleton(provider => new ControlService(store, provider.GetRequiredService<PresenceRegistry>(), provider.GetRequiredService<DirectoryHub>(),
            provider.GetRequiredService<PublicationService>(), provider.GetRequiredService<RelayHub>(), time, options.ConnectionPolicy,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Control"))
        {
            Metrics = metrics,
            TelemetrySettings = provider.GetRequiredService<TelemetrySettingsService>(),
            Updates = provider.GetRequiredService<UpdateCampaignService>(),
            Automations = provider.GetRequiredService<FleetAutomationService>(),
            Hooks = provider.GetRequiredService<HookService>(),
            Stopping = provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
            Statistics = provider.GetRequiredService<StatisticsService>(),
            Health = provider.GetRequiredService<HealthService>(),
        });

        var app = builder.Build();
        app.UseMiddleware<HttpSpanEnricher>();
        app.UseMiddleware<ProblemDetailsMiddleware>();
        app.Use(async (context, next) =>
        {
            var proxies = context.RequestServices.GetRequiredService<TrustedProxies>();
            context.Request.Scheme = proxies.SchemeOf(context.Connection.RemoteIpAddress, context.Request.Headers, context.Request.Scheme);
            context.Request.Host = proxies.HostOf(context.Connection.RemoteIpAddress, context.Request.Headers, context.Request.Host);
            await next(context).ConfigureAwait(false);
        });
        app.UseStatusCodePages();
        app.UseWebSockets();
        app.UseRouting();
        // Before the body is read or a signature checked: a flood costs the server as little as possible.
        app.UseMiddleware<RateLimitMiddleware>();
        app.UseMiddleware<DeviceAuthenticationMiddleware>();
        app.MapOpenApi();
        ApiEndpoints.Map(app);
        HealthEndpoints.Map(app);
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
        var state = new ServerState(Environment.ProcessId, Addresses.Select(static address => address.ToString().TrimEnd('/')).ToArray(), startedAt, ServerVersion.Current,
            CertificatePin);
        SafeFile.WriteAllText(_paths.StatePath, JsonSerializer.Serialize(state, ServerStateJsonContext.Default.ServerState));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ServerState))]
internal sealed partial class ServerStateJsonContext : JsonSerializerContext;
