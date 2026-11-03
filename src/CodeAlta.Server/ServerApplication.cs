using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;
using CodeAlta.Server.Api;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XenoAtom.CommandLine;

namespace CodeAlta.Server;

/// <summary>The exit codes of <c>altaserver</c>.</summary>
public static class ServerExitCodes
{
    /// <summary>The command succeeded; for <c>status</c>, a server runs.</summary>
    public const int Success = 0;

    /// <summary>The command failed; for <c>status</c>, no server runs.</summary>
    public const int Failure = 1;

    /// <summary>The command line is not valid.</summary>
    public const int InvalidUsage = 2;
}

/// <summary>The commands of <c>altaserver</c>: <c>run</c>, <c>status</c>, <c>setup-code</c>, <c>update</c>.</summary>
public sealed class ServerApplication
{
    private const string _ = "";

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private string[] _args = [];

    /// <summary>Initializes a new instance of the <see cref="ServerApplication"/> class.</summary>
    /// <param name="output">Where the commands write.</param>
    /// <param name="error">Where the errors go.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ServerApplication(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        _output = output;
        _error = error;
    }

    /// <summary>Gets what changes the options of the launcher <c>run</c> hands over to the selected version with, for tests; null for none.</summary>
    internal Func<UpdateLauncherOptions, UpdateLauncherOptions>? ConfigureLauncher { get; init; }

    /// <summary>Gets what changes the options of the self-update of a running server, for tests; null for none.</summary>
    internal Func<ServerUpdatesOptions, ServerUpdatesOptions>? ConfigureUpdates { get; init; }

    /// <summary>Gets the HTTP handler of the feeds of <c>update</c>, for tests; null for the usual one.</summary>
    internal HttpMessageHandler? UpdateHandler { get; init; }

    /// <summary>Gets the folder of the application of this process, for tests; null for <see cref="AppContext.BaseDirectory"/>.</summary>
    internal string? ApplicationDirectory { get; init; }

    /// <summary>Gets what a test is told once <c>run</c> started its server, with what stops it; null for nothing.</summary>
    internal Action<CoordinatorServer, Action>? Started { get; init; }

    /// <summary>Gets what changes the options of the server <c>run</c> starts, for tests; null for none.</summary>
    internal Func<CoordinatorServerOptions, CoordinatorServerOptions>? ConfigureServer { get; init; }

    /// <summary>Runs a command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code (<see cref="ServerExitCodes"/>).</returns>
    public async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        _args = args;
        if (args.Length == 0) args = ["--help"];
        return await CreateApp().RunAsync(args, new CommandRunConfig { Out = _output, Error = _error }).ConfigureAwait(false);
    }

    private CommandApp CreateApp()
    {
        var run = new RunOptions();
        var runCommand = new Command("run", "Run the server in the foreground (logs to the console)")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Serves the REST API (/api/v1, its OpenAPI document at /openapi/v1.json)",
            "and the control connection of devices (/fleet/v1) on http://127.0.0.1:2586.",
            "Plain HTTP is for the loopback. To reach it from other machines, keep it on",
            "the loopback behind Tailscale Serve, Caddy or the TLS of your platform (the",
            "usual way), or give it a certificate and an https:// address.",
            _,
            "Until it has an owner, the server prints a one-time setup code and accepts",
            "its setup from this machine only (altaserver setup-code prints it again).",
            "Ctrl+C or SIGTERM stops it.",
            _,
            "Options:",
            { "port=", "Listen on http://127.0.0.1:{PORT} (2586 by default)", v => run.Port = v },
            { "urls=", "The {ADDRESSES} to listen on, separated by ';' (http:// or https://)", v => run.Urls = v },
            { "certificate=", "A PKCS #12 {FILE} for the https:// addresses", v => run.Certificate = v },
            { "certificate-password-env=", "The environment {VARIABLE} that holds the certificate's password", v => run.CertificatePasswordVariable = v },
        };
        AddDataRoot(runCommand, run);
        runCommand.Add((_, _) => RunServerAsync(run));

        var status = new DataRootOptions();
        var statusCommand = new Command("status", "Tell whether a server runs on the data root, and where")
        {
            new CommandUsage("Usage: {NAME} [options]"),
            _,
            "Exit 0 when a server holds the data root, 1 when none does. Prints its",
            "addresses, its version and whether it has an owner.",
            _,
            "Options:",
        };
        AddDataRoot(statusCommand, status);
        statusCommand.Add((_, _) => StatusAsync(status));

        var setup = new SetupCodeOptions();
        var setupCommand = new Command("setup-code", "Print the one-time setup code of a server without an owner")
        {
            new CommandUsage("Usage: {NAME} [--renew] [options]"),
            _,
            "Prints the code that sets the server up, making one when none waits or it",
            "expired. The code works once, for 30 minutes, from this machine only;",
            "five wrong attempts drop it. Once the server has an owner, there is no code.",
            _,
            "Options:",
            { "renew", "Make a new code in place of the one waiting", v => setup.Renew = v is not null },
        };
        AddDataRoot(setupCommand, setup);
        setupCommand.Add((_, _) => SetupCodeAsync(setup));

        var update = new UpdateOptions();
        var updateCommand = new Command("update", "Update the server to a new version")
        {
            new CommandUsage("Usage: {NAME} [--version <v>] [--source <feed>] [--check] [--now] [options]"),
            _,
            "Downloads the package of this OS and architecture from a NuGet feed",
            "(nuget.org by default), checks its hash, and puts it beside the others in",
            "versions/ of the data root. The running server copies its database to",
            "backups/, stops and starts the new version; without a running server,",
            "the next altaserver run starts it. The new version runs on trial: if it",
            "does not serve within 2 minutes, or crashes, the previous one comes back",
            "with the database copied before the switch, and the new one is marked",
            "bad. No .NET SDK is needed. Without --version: the latest release (a",
            "prerelease when one runs). update.json of the data root sets the feed",
            "and the allowed feeds: { \"source\": \"<feed>\", \"allowedSources\": [...] }.",
            _,
            "Options:",
            { "version=", "The {VERSION} to install, also an older one", v => update.Version = v },
            { "source=", "The {FEED}: a NuGet v3 index (https://) or a folder", v => update.Source = v },
            { "sha512=", "The {HASH} the package must have (base64 or hexadecimal)", v => update.Sha512 = v },
            { "check", "Only tell whether an update is available", v => update.Check = v is not null },
            { "now", "Accepted for symmetry with altanode: the server never waits", v => update.Now = v is not null },
            { "prerelease", "Take a prerelease as the latest version", v => update.Prerelease = v is not null },
        };
        AddDataRoot(updateCommand, update);
        updateCommand.Add((_, _) => UpdateAsync(update));

        return new CommandApp("altaserver", config: new CommandConfig { OutputFactory = static _ => DefaultCommandOutput.Instance })
        {
            new CommandUsage("Usage: {NAME} <command> [options]"),
            new CommandUsage("Usage: {NAME} <command> --help"),
            _,
            "CodeAlta Server: the coordinator of a CodeAlta fleet. It knows the people,",
            "devices and machines of an organization and the facts of their sessions;",
            "it runs no agent and reads no session content.",
            _,
            "Commands:",
            runCommand,
            statusCommand,
            setupCommand,
            updateCommand,
            _,
            "Options:",
            new HelpOption(),
            new VersionOption(),
        };
    }

    private static void AddDataRoot(Command command, DataRootOptions options)
    {
        command.Add("data-root=", "The data {DIRECTORY} of the server in place of ~/.altaserver (an absolute path)", v => options.DataRoot = v);
        command.Add(new HelpOption());
    }

    private async ValueTask<int> RunServerAsync(RunOptions options)
    {
        if (!options.TryResolve(_error, out var paths)) return ServerExitCodes.InvalidUsage;
        var urls = new List<string>();
        if (options.Urls is not null) urls.AddRange(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (options.Port is not null)
        {
            if (!int.TryParse(options.Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                await _error.WriteLineAsync($"--port needs a port from 1 to 65535: '{options.Port}'.").ConfigureAwait(false);
                return ServerExitCodes.InvalidUsage;
            }

            urls.Add($"http://127.0.0.1:{port}");
        }

        string? password = null;
        if (options.CertificatePasswordVariable is not null)
        {
            password = Environment.GetEnvironmentVariable(options.CertificatePasswordVariable);
            if (password is null)
            {
                await _error.WriteLineAsync($"The environment variable '{options.CertificatePasswordVariable}' is not set.").ConfigureAwait(false);
                return ServerExitCodes.InvalidUsage;
            }
        }

        // The installed entry point (the tool) hands over to the version altaserver update selected, and passes its exit
        // code through; a switch that waited for the next start is taken first. Not while another server holds the data
        // root: this run is refused below as before, and the version on trial is not blamed for that refusal.
        var store = new UpdateVersionStore(paths!.VersionsRoot);
        if (!UpdateLauncher.IsSupervised && !store.Contains(ApplicationDirectory ?? AppContext.BaseDirectory) && !CoordinatorServer.IsRunning(paths))
        {
            try
            {
                // Without a running server to copy it, the database is copied here: a switch is never selected without it.
                store.TakePending(UpdateProduct.Server, target => ServerUpdates.BackupStopped(paths, target));
            }
            catch (UpdateException exception)
            {
                await _error.WriteLineAsync($"altaserver: the versions of {store.Root} could not be read: {exception.Message}").ConfigureAwait(false);
            }

            // A rollback whose database is not restored yet goes through the launcher too: it restores first, or starts nothing.
            if (store.Read() is { } selected && (selected.Current is not null || selected.RollingBack is not null)) return await RunLauncherAsync(paths, store).ConfigureAwait(false);
        }

        CoordinatorServer server;
        ServerUpdates? updates = null;
        try
        {
            var serverOptions = new CoordinatorServerOptions
            {
                DataRoot = paths.DataRoot,
                Urls = urls,
                CertificatePath = options.Certificate,
                CertificatePassword = password,
                UpdateStatus = () => updates?.Status(),
            };
            server = await CoordinatorServer.StartAsync(ConfigureServer?.Invoke(serverOptions) ?? serverOptions).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or System.Security.Cryptography.CryptographicException)
        {
            await _error.WriteLineAsync($"altaserver: {exception.Message}").ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }

        using var restart = new CancellationTokenSource();
        await using (server.ConfigureAwait(false))
        {
            await _output.WriteLineAsync($"CodeAlta Server {ServerVersion.Current} listens on {string.Join(", ", server.Addresses.Select(static address => address.ToString().TrimEnd('/')))} (data root {paths.DataRoot}).").ConfigureAwait(false);
            if (server.SetupCode is { } code)
            {
                await _output.WriteLineAsync($"No owner yet. Setup code: {code.Code} (until {code.ExpiresAt.ToLocalTime():t}, from this machine only; altaserver setup-code prints it again).")
                    .ConfigureAwait(false);
            }

            // The self-update: a version on trial confirms itself once it serves; a switch altaserver update asks for
            // copies the database, then stops the server so that its launcher starts the new version.
            if (UpdateVersion.TryParse(ServerVersion.Current, out var running))
            {
                var logger = server.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeAlta.Server.Update");
                var updateOptions = new ServerUpdatesOptions
                {
                    Store = store, Running = running, Installed = InstalledVersion(), Healthy = token => ServesAsync(server, token),
                    Backup = async (target, token) =>
                    {
                        var backup = ServerUpdates.BackupPath(paths, target, DateTimeOffset.UtcNow);
                        await server.BackupDatabaseAsync(backup, token).ConfigureAwait(false);
                        return backup;
                    },
                    RequestRestart = restart.Cancel, PolicyPath = paths.UpdatePolicyPath, Log = message => logger.LogInformation("{Message}", message),
                };
                updates = new ServerUpdates(ConfigureUpdates?.Invoke(updateOptions) ?? updateOptions);
                updates.Start();
            }

            Started?.Invoke(server, restart.Cancel);
            await server.WaitForShutdownAsync(restart.Token).ConfigureAwait(false);
            if (updates is not null) await updates.DisposeAsync().ConfigureAwait(false);
            if (updates?.RestartRequested == true) await _output.WriteLineAsync("Stopping CodeAlta Server to start its new version...").ConfigureAwait(false);
        }

        if (updates?.RestartRequested != true) return ServerExitCodes.Success;
        // A launcher waits for this process and starts the newly selected version; without one, this process becomes it.
        if (UpdateLauncher.IsSupervised) return UpdateLauncher.RestartExitCode;
        return await RunLauncherAsync(paths, store).ConfigureAwait(false);
    }

    // The launcher hop: the selected version runs as a child process of this one, which ends with its exit code. A
    // rollback restores the database copied before the switch.
    private async Task<int> RunLauncherAsync(ServerPaths paths, UpdateVersionStore store)
    {
        var options = new UpdateLauncherOptions
        {
            Store = store, Product = UpdateProduct.Server, Arguments = _args, Installed = InstalledCommand(), InstalledVersion = InstalledVersion(),
            Log = message => _error.WriteLine("altaserver: " + message),
            Backup = target => ServerUpdates.BackupStopped(paths, target),
            RolledBack = (trial, _) =>
            {
                if (trial.Backup is { } backup)
                {
                    ServerUpdates.RestoreBackup(paths, backup);
                    _error.WriteLine($"altaserver: the database copied before {trial.Version} was restored ({backup}).");
                }

                return Task.CompletedTask;
            },
        };
        if (ConfigureLauncher is not null) options = ConfigureLauncher(options);
        return await new UpdateLauncher(options).RunAsync().ConfigureAwait(false);
    }

    // How this process was started, to start the installed entry point again when the pointer returns to it.
    private UpdateCommand InstalledCommand()
    {
        var path = Environment.ProcessPath ?? "altaserver";
        return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new UpdateCommand(path, [Path.Combine(ApplicationDirectory ?? AppContext.BaseDirectory, UpdateProduct.Server.AssemblyFileName)])
            : new UpdateCommand(path, []);
    }

    // The version of the installed entry point: this one, unless this process runs a version of a folder of versions.
    private string? InstalledVersion()
    {
        if (UpdateLauncher.SupervisorInstalledVersion is { } passed) return passed;
        var directory = Path.TrimEndingDirectorySeparator(ApplicationDirectory ?? AppContext.BaseDirectory);
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "versions", StringComparison.OrdinalIgnoreCase) ? null : ServerVersion.Current;
    }

    // Whether the server serves: it answers its own health request, on its first address (a wildcard address through
    // the loopback). Its own certificate is not checked: the request carries nothing and only proves that Kestrel answers.
    private static async Task<bool> ServesAsync(CoordinatorServer server, CancellationToken cancellationToken)
    {
        if (server.Addresses.FirstOrDefault() is not { } address) return false;
        var builder = new UriBuilder(address);
        if (IPAddress.TryParse(builder.Host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
            builder.Host = ip.Equals(IPAddress.Any) ? "127.0.0.1" : "[::1]";
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var health = await client.GetFromJsonAsync(new Uri(builder.Uri, "api/v1/server/health"), ServerApiJsonContext.Default.ServerHealthDto, cancellationToken).ConfigureAwait(false);
            return health?.Status == "ok";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private async ValueTask<int> UpdateAsync(UpdateOptions options)
    {
        if (!options.TryResolve(_error, out var paths)) return ServerExitCodes.InvalidUsage;
        if (options.Check && options.Now)
        {
            await _error.WriteLineAsync("altaserver: --check changes nothing: --now cannot go with it.").ConfigureAwait(false);
            return ServerExitCodes.InvalidUsage;
        }

        var store = new UpdateVersionStore(paths!.VersionsRoot);
        try
        {
            var state = store.Read();
            var installed = InstalledVersion();
            var running = UpdateVersion.Parse(state.Current ?? installed ?? ServerVersion.Current);
            using var http = UpdateHandler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) } : new HttpClient(UpdateHandler, disposeHandler: false);
            var updater = new SelfUpdater(new SelfUpdaterOptions
            {
                Product = UpdateProduct.Server, Store = store, Running = running, Http = http, Policy = UpdateSourcePolicy.Load(paths.UpdatePolicyPath),
            });
            var request = new UpdateRequest { Version = options.Version, Source = options.Source, ExpectedSha512 = options.Sha512, Prerelease = options.Prerelease, Now = options.Now };
            var check = await updater.CheckAsync(request, CancellationToken.None).ConfigureAwait(false);
            await _output.WriteLineAsync($"Feed: {check.Source} ({check.PackageId}).").ConfigureAwait(false);
            await _output.WriteLineAsync($"Runs: {running}{(installed is not null && state.Current is not null ? $" (installed {installed})" : "")}; latest: {check.Latest?.ToString() ?? "none"}.")
                .ConfigureAwait(false);
            if (check.Note is { } note) await _output.WriteLineAsync(note).ConfigureAwait(false);
            if (check.Target is not { } target) return ServerExitCodes.Success;
            if (options.Check)
            {
                await _output.WriteLineAsync($"An update to {target} is available: altaserver update{(options.Version is null ? "" : " --version " + target)} installs it.").ConfigureAwait(false);
                return ServerExitCodes.Success;
            }

            var staged = await updater.StageAsync(check, request, CancellationToken.None).ConfigureAwait(false);
            await _output.WriteLineAsync(staged.AlreadyStaged
                ? $"{target} is staged already in {staged.Path} (its package matches: {staged.Checked})."
                : $"Downloaded {check.PackageId} {target}, hash checked by the {staged.Checked}, staged in {staged.Path}.").ConfigureAwait(false);
            store.RequestSwitch(target, options.Now, "local");
            if (!CoordinatorServer.IsRunning(paths))
            {
                await _output.WriteLineAsync($"No server runs on {paths.DataRoot}: the next altaserver run starts {target} on trial.").ConfigureAwait(false);
                return ServerExitCodes.Success;
            }

            await _output.WriteLineAsync($"The server of {paths.DataRoot} switches to {target}.").ConfigureAwait(false);
            var (outcome, message) = await UpdateFollow.FollowAsync(store, target, UpdateLauncher.DefaultTrialLimit + TimeSpan.FromSeconds(60),
                line => _output.WriteLineAsync(line), CancellationToken.None).ConfigureAwait(false);
            switch (outcome)
            {
                case UpdateFollowOutcome.Confirmed:
                    await _output.WriteLineAsync("CodeAlta Server " + message).ConfigureAwait(false);
                    return ServerExitCodes.Success;
                case UpdateFollowOutcome.Failed:
                case UpdateFollowOutcome.Withdrawn:
                    await _error.WriteLineAsync("altaserver update: " + message).ConfigureAwait(false);
                    return ServerExitCodes.Failure;
                default:
                    await _output.WriteLineAsync(message + " altaserver status shows where it stands.").ConfigureAwait(false);
                    return ServerExitCodes.Success;
            }
        }
        catch (UpdateException exception)
        {
            await _error.WriteLineAsync($"altaserver update: {exception.Message}").ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }
    }

    // The versions of the data root, for status: the running server's own, else what the pointer selects.
    private async Task WriteVersionsAsync(ServerPaths paths, string? runningVersion)
    {
        var state = new UpdateVersionStore(paths.VersionsRoot).Read();
        if (state.RollingBack is { } rollback)
        {
            await _output.WriteLineAsync($"{rollback.Trial.Version} was rolled back, but the database copied before it ({rollback.Trial.Backup}) is not restored yet"
                + $"{(rollback.Error is { } error ? ": " + error : "")}. altaserver run restores it before it starts the server.").ConfigureAwait(false);
        }

        var installed = InstalledVersion();
        var running = runningVersion ?? state.Current ?? installed;
        if (running is null) return;
        foreach (var line in UpdateStatusReport.Lines(UpdateStatusReport.Describe(state, running, installed), "altaserver"))
            await _output.WriteLineAsync(line).ConfigureAwait(false);
    }

    private async ValueTask<int> StatusAsync(DataRootOptions options)
    {
        if (!options.TryResolve(_error, out var paths)) return ServerExitCodes.InvalidUsage;
        if (!CoordinatorServer.IsRunning(paths!))
        {
            await _output.WriteLineAsync($"No server runs on {paths!.DataRoot}.").ConfigureAwait(false);
            await WriteVersionsAsync(paths, null).ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }

        var state = CoordinatorServer.ReadState(paths!);
        if (state is null)
        {
            await _output.WriteLineAsync($"A server holds {paths!.DataRoot} but has not written its state yet (it is starting).").ConfigureAwait(false);
            await WriteVersionsAsync(paths, null).ConfigureAwait(false);
            return ServerExitCodes.Success;
        }

        await WriteVersionsAsync(paths!, state.Version).ConfigureAwait(false);

        await _output.WriteLineAsync($"CodeAlta Server {state.Version}, process {state.ProcessId}, since {state.StartedAt.ToLocalTime():g}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Listens on {string.Join(", ", state.Urls)}").ConfigureAwait(false);
        var address = state.Urls.FirstOrDefault(static url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? state.Urls.FirstOrDefault();
        if (address is not null)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var health = await client.GetFromJsonAsync($"{address}/api/v1/server/health", ServerApiJsonContext.Default.ServerHealthDto).ConfigureAwait(false);
                await _output.WriteLineAsync(health is null ? "Health: no answer" : $"Health: {health.Status}, {(health.SetUp ? "set up" : "no owner yet (altaserver setup-code)")}, schema {health.SchemaVersion}")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                await _output.WriteLineAsync($"Health: no answer from {address} ({exception.Message})").ConfigureAwait(false);
            }
        }

        return ServerExitCodes.Success;
    }

    private async ValueTask<int> SetupCodeAsync(SetupCodeOptions options)
    {
        if (!options.TryResolve(_error, out var paths)) return ServerExitCodes.InvalidUsage;
        if (File.Exists(paths!.DatabasePath))
        {
            await using var store = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions { DatabasePath = paths.DatabasePath, ReadOnly = true });
            await store.InitializeAsync().ConfigureAwait(false);
            if (await store.GetSchemaVersionAsync().ConfigureAwait(false) > 0 && await store.GetOrganizationAsync().ConfigureAwait(false) is not null)
            {
                await _error.WriteLineAsync("The server already has an owner: there is no setup code.").ConfigureAwait(false);
                return ServerExitCodes.Failure;
            }
        }

        var codes = new SetupCodeStore(paths.SetupCodePath, TimeProvider.System);
        var lifetime = new CoordinatorServerOptions { DataRoot = paths.DataRoot }.SetupCodeLifetime;
        var code = options.Renew ? codes.Create(lifetime) : codes.GetOrCreate(lifetime);
        await _output.WriteLineAsync(code.Code).ConfigureAwait(false);
        await _output.WriteLineAsync($"Valid until {code.ExpiresAt.ToLocalTime():g}, once, from this machine only.").ConfigureAwait(false);
        return ServerExitCodes.Success;
    }

    private class DataRootOptions
    {
        public string? DataRoot { get; set; }

        public bool TryResolve(TextWriter error, out ServerPaths? paths)
        {
            paths = null;
            var root = DataRoot ?? ServerPaths.GetDefaultDataRoot();
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                error.WriteLine($"--data-root needs an absolute path: '{DataRoot}'.");
                return false;
            }

            paths = new ServerPaths(root);
            return true;
        }
    }

    private sealed class RunOptions : DataRootOptions
    {
        public string? Port { get; set; }

        public string? Urls { get; set; }

        public string? Certificate { get; set; }

        public string? CertificatePasswordVariable { get; set; }
    }

    private sealed class SetupCodeOptions : DataRootOptions
    {
        public bool Renew { get; set; }
    }

    private sealed class UpdateOptions : DataRootOptions
    {
        public string? Version { get; set; }

        public string? Source { get; set; }

        public string? Sha512 { get; set; }

        public bool Check { get; set; }

        public bool Now { get; set; }

        public bool Prerelease { get; set; }
    }
}
