using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
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

    /// <summary>For <c>run</c>: another server, or another <c>run</c> that starts one, holds the data root.</summary>
    public const int DataRootInUse = 3;
}

/// <summary>
/// The commands of <c>altaserver</c>: <c>run</c>, <c>status</c>, <c>install</c>, <c>uninstall</c>, <c>setup-code</c>,
/// <c>update</c>.
/// </summary>
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

    /// <summary>
    /// Gets what reads the machine <c>install</c>, <c>uninstall</c> and <c>status</c> work on, for tests (temporary folders,
    /// recorded commands); null for this machine.
    /// </summary>
    internal Func<ServiceEnvironment?>? ServiceEnvironment { get; init; }

    /// <summary>Gets what finds the executable a service starts, for tests; null for the one of this process.</summary>
    internal Func<ServiceExecutable?>? ServiceExecutable { get; init; }

    /// <summary>Gets how often a running server looks for the request to stop of <c>uninstall</c>.</summary>
    internal TimeSpan StopRequestInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how long <c>uninstall</c> waits for the server it asked to stop.</summary>
    internal TimeSpan StopPatience { get; init; } = TimeSpan.FromSeconds(60);

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
            "usual way), or give it an https:// address with a certificate: a file of",
            "yours (--certificate), or one it makes and keeps (--own-certificate), which",
            "no browser trusts and its devices pin: the address to give them, with the",
            "pin, is printed at the start and by altaserver status. An http:// address",
            "beyond the loopback is refused unless --allow-insecure-http forces it.",
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
            { "own-certificate", "Without --certificate, make (once) and keep a certificate of the server's own for the https:// addresses, which its devices pin", v => run.OwnCertificate = v is not null },
            { "allow-insecure-http", "Allow http:// addresses beyond the loopback, on a network you trust", v => run.AllowInsecureHttp = v is not null },
            { "trusted-proxy=", "The {ADDRESSES} of the reverse proxies in front of the server, separated by ';' (127.0.0.1;::1 for Tailscale Serve or Caddy here): their X-Forwarded-For names the caller the limits count", v => run.TrustedProxies = v },
        };
        runCommand.Add("service", "Answer the Windows Service Control Manager (what install --system starts)", v => run.Service = v is not null);
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

        var install = new InstallOptions();
        var installCommand = new Command("install", "Install and start the service of this OS that runs the server")
        {
            new CommandUsage("Usage: {NAME} [--user | --system [--account <name> [--password-stdin]]] [--dry-run] [run options]"),
            _,
            "Installs the service that runs altaserver run with these options, so that",
            "the server starts again after a restart, and starts it: a task at the",
            "logon of the user on Windows (without a window, started again if it",
            "stops), a LaunchAgent on macOS, a systemd --user unit on Linux, with",
            "lingering turned on so that it starts at boot without a login. One",
            "service per data root; running install again replaces it with the new",
            "options. --system installs a service started at boot without a login: a",
            "LaunchDaemon or a system unit that runs as --account (with sudo), or a",
            "Windows service (from an elevated terminal) that runs as its own account",
            "NT SERVICE\\altaserver with its data root under %ProgramData%, or as",
            "--account with its password on standard input. A .NET tool runs from its",
            "shim, so the service follows dotnet tool update, and altaserver update",
            "switches versions without the service.",
            _,
            "Options:",
            { "port=", "Listen on http://127.0.0.1:{PORT} (2586 by default)", v => install.Port = v },
            { "urls=", "The {ADDRESSES} to listen on, separated by ';' (http:// or https://)", v => install.Urls = v },
            { "certificate=", "A PKCS #12 {FILE} for the https:// addresses", v => install.Certificate = v },
            { "certificate-password-env=", "The environment {VARIABLE} that holds the certificate's password", v => install.CertificatePasswordVariable = v },
            { "own-certificate", "Without --certificate, a certificate of the server's own, which its devices pin", v => install.OwnCertificate = v is not null },
            { "allow-insecure-http", "Allow http:// addresses beyond the loopback, on a network you trust", v => install.AllowInsecureHttp = v is not null },
            { "trusted-proxy=", "The {ADDRESSES} of the reverse proxies in front of the server, separated by ';'", v => install.TrustedProxies = v },
            { "user", "A service of the current user (the default)", v => install.User = v is not null },
            { "system", "A service of the system, started at boot, that runs as --account", v => install.System = v is not null },
            { "account=", "The {ACCOUNT} a --system service runs as", v => install.Account = v },
            { "password-stdin", "Read the password of --account from standard input (Windows)", v => install.PasswordStdin = v is not null },
            { "dry-run", "Print what would be installed, change nothing", v => install.DryRun = v is not null },
        };
        AddDataRoot(installCommand, install);
        installCommand.Add((_, _) => InstallAsync(install));

        var uninstall = new InstallOptions();
        var uninstallCommand = new Command("uninstall", "Stop and remove the service; keep the data root")
        {
            new CommandUsage("Usage: {NAME} [--user | --system --account <name>] [--dry-run] [options]"),
            _,
            "Removes the service install set up for the data root and stops its",
            "server; the data root, its database and its versions stay.",
            _,
            "Options:",
            { "user", "The service of the current user (the default)", v => uninstall.User = v is not null },
            { "system", "The service of the system that runs as --account", v => uninstall.System = v is not null },
            { "account=", "The {ACCOUNT} of the --system service", v => uninstall.Account = v },
            { "dry-run", "Print what would be removed, change nothing", v => uninstall.DryRun = v is not null },
        };
        AddDataRoot(uninstallCommand, uninstall);
        uninstallCommand.Add((_, _) => UninstallAsync(uninstall));

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

        var backups = new BackupsOptions();
        var backupsCommand = new Command("backups", "List the backups of the database, and delete those that keep secrets in clear")
        {
            new CommandUsage("Usage: {NAME} [--delete-unsealed] [options]"),
            _,
            "Lists the copies of the database in backups/ of the data root (taken before",
            "each update) and whether each keeps the client secret of the company's",
            "sign-in or the headers of the telemetry collector in clear: a version",
            "before the server sealed them took those. --delete-unsealed deletes them,",
            "but the one a version on trial rolls back to, which goes once the trial",
            "ends. A copy taken since holds them sealed: it opens with the key ring of",
            "keys/ only, which a backup of the data root keeps beside it.",
            _,
            "Options:",
            { "delete-unsealed", "Delete the backups that keep secrets in clear", v => backups.DeleteUnsealed = v is not null },
        };
        AddDataRoot(backupsCommand, backups);
        backupsCommand.Add((_, _) => BackupsAsync(backups));

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
            installCommand,
            uninstallCommand,
            setupCommand,
            updateCommand,
            backupsCommand,
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

    // The stop of the Windows service this process runs as (run --service): the Service Control Manager's stop or shutdown.
    private CancellationToken _serviceStop;

    private async ValueTask<int> RunServerAsync(RunOptions options)
    {
        if (options.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                await _error.WriteLineAsync("altaserver: --service is for the Windows service that altaserver install --system registers.").ConfigureAwait(false);
                return ServerExitCodes.InvalidUsage;
            }

            options.Service = false;
            // The process answers the Service Control Manager; its stop becomes the graceful stop of the server, as SIGTERM does elsewhere.
            return WindowsServiceHost.Run(ServiceDefinition.Noun(UpdateProduct.Server), token =>
            {
                _serviceStop = token;
                return RunServerAsync(options).AsTask();
            });
        }

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
        // code through; a switch that waited for the next start is taken first. It holds the lease of the launcher from
        // here to its end, so that a second run never selects, starts or rolls back a version while this one does: it is
        // refused at once, even in the moment the version this run started has not taken the data root yet. Not while
        // another server holds the data root: this run is refused below as before, and the version on trial is not blamed.
        var store = new UpdateVersionStore(paths!.VersionsRoot);
        UpdateLauncherLease? lease = null;
        if (!UpdateLauncher.IsSupervised && !store.Contains(ApplicationDirectory ?? AppContext.BaseDirectory))
        {
            try
            {
                lease = store.TryAcquireLauncherLease();
            }
            catch (UpdateException exception)
            {
                await _error.WriteLineAsync($"altaserver: {exception.Message}").ConfigureAwait(false);
                return ServerExitCodes.Failure;
            }

            if (lease is null)
            {
                await _error.WriteLineAsync($"altaserver: another altaserver run holds the data root '{paths.DataRoot}' (it runs the server, or starts it).").ConfigureAwait(false);
                return ServerExitCodes.DataRootInUse;
            }
        }

        using var heldLease = lease;
        if (lease is not null && !CoordinatorServer.IsRunning(paths))
        {
            try
            {
                // An installed entry point the owner updated past the selected version runs itself (dotnet tool update).
                if (store.ReconcileInstalled(InstalledVersion()) is { } replaced)
                    await _error.WriteLineAsync($"altaserver: the installed altaserver {InstalledVersion()} is newer than {replaced}, which was selected before it: it runs instead.").ConfigureAwait(false);
                // Without a running server to copy it, the database is copied here: a switch is never selected without it.
                store.TakePending(UpdateProduct.Server, target => ServerUpdates.BackupStopped(paths, target));
            }
            catch (UpdateException exception)
            {
                await _error.WriteLineAsync($"altaserver: the versions of {store.Root} could not be read: {exception.Message}").ConfigureAwait(false);
            }

            // A rollback whose database is not restored yet goes through the launcher too: it restores first, or starts nothing.
            if (store.Read() is { } selected && (selected.Current is not null || selected.RollingBack is not null)) return await RunLauncherAsync(paths, store, lease).ConfigureAwait(false);
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
                AllowInsecureHttp = options.AllowInsecureHttp,
                OwnCertificate = options.OwnCertificate,
                TrustedProxies = Proxies(options.TrustedProxies),
                UpdateStatus = () => updates?.Status(),
                // The last step of a campaign of the fleet: the same self-update as altaserver update, with the signed hash.
                ApplyUpdate = (request, requestedBy, token) => updates is { } self
                    ? self.ApplyAsync(request, requestedBy, token)
                    : throw new UpdateException(UpdateErrorCodes.StateUnavailable, "The self-update of this server is not running."),
                // A campaign a new policy revokes during the server's step: its switch, while it waits (only that one).
                CancelUpdate = (requestedBy, reason) => store.CancelPending(reason, requestedBy),
            };
            server = await CoordinatorServer.StartAsync(ConfigureServer?.Invoke(serverOptions) ?? serverOptions).ConfigureAwait(false);
        }
        catch (DataRootInUseException exception)
        {
            // Under a launcher, this exit code tells it that this version did not run: its trial is not failed.
            await _error.WriteLineAsync($"altaserver: {exception.Message}").ConfigureAwait(false);
            return ServerExitCodes.DataRootInUse;
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
            if (server.CertificatePin is { } pin)
                await _output.WriteLineAsync($"Certificate: {pin} (devices reach the server at its https:// address followed by #pin={pin}).").ConfigureAwait(false);
            // Copies an older version took before the server sealed its secrets keep them in clear: said once, with what to do.
            if (ServerBackups.FindUnsealed(paths) is { Count: > 0 } unsealed)
            {
                await _error.WriteLineAsync($"altaserver: {unsealed.Count} backup{(unsealed.Count == 1 ? "" : "s")} in {paths.BackupsRoot} keep the client secret of the company's sign-in or the " +
                    "headers of the telemetry collector in clear (a version before the server sealed them took them): altaserver backups --delete-unsealed deletes them.")
                    .ConfigureAwait(false);
            }

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

            // altaserver uninstall asks this server to stop through a file of its data root: on Windows, where the service is a
            // task at logon, no signal reaches it. A request left from before this start is not for it. A version on trial is
            // confirmed within seconds of serving: one stopped before is rolled back by its launcher, as one that exited.
            TryDelete(paths.StopRequestPath);
            using var stopRequested = new CancellationTokenSource();
            // The Windows service's stop is a graceful stop, as altaserver uninstall asks it.
            using var serviceStop = _serviceStop.Register(stopRequested.Cancel);
            await using var stopWatch = new Timer(_ => { if (File.Exists(paths.StopRequestPath)) stopRequested.Cancel(); }, null, StopRequestInterval, StopRequestInterval);
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(restart.Token, stopRequested.Token);
            Started?.Invoke(server, restart.Cancel);
            await server.WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
            if (stopRequested.IsCancellationRequested && !restart.IsCancellationRequested)
            {
                TryDelete(paths.StopRequestPath);
                await _output.WriteLineAsync("Stopping CodeAlta Server: altaserver uninstall asked it to.").ConfigureAwait(false);
            }

            if (updates is not null) await updates.DisposeAsync().ConfigureAwait(false);
            if (updates?.RestartRequested == true) await _output.WriteLineAsync("Stopping CodeAlta Server to start its new version...").ConfigureAwait(false);
        }

        if (updates?.RestartRequested != true) return ServerExitCodes.Success;
        // A launcher waits for this process and starts the newly selected version; without one, this process becomes it.
        if (UpdateLauncher.IsSupervised) return UpdateLauncher.RestartExitCode;
        return await RunLauncherAsync(paths, store, lease).ConfigureAwait(false);
    }

    // The launcher hop: the selected version runs as a child process of this one, which ends with its exit code. A
    // rollback restores the database copied before the switch.
    private async Task<int> RunLauncherAsync(ServerPaths paths, UpdateVersionStore store, UpdateLauncherLease? lease)
    {
        var options = new UpdateLauncherOptions
        {
            // The version runs without --service: the Service Control Manager talks to this process only.
            Store = store, Product = UpdateProduct.Server, Arguments = [.. _args.Where(static argument => argument != ServiceDefinition.WindowsServiceOption)],
            Installed = InstalledCommand(), InstalledVersion = InstalledVersion(), Lease = lease, HeldExitCode = ServerExitCodes.DataRootInUse,
            Log = message => _error.WriteLine("altaserver: " + message),
            Stop = _serviceStop,
            // Windows has no SIGTERM: the version is asked to stop through its data root, as altaserver uninstall asks it.
            GracefulStop = OperatingSystem.IsWindows() ? _ => RequestStopThroughDataRoot(paths) : null,
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
        var builder = new UriBuilder(Reachable(address));
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

    // An address of the server a client of this machine connects to: a wildcard address (0.0.0.0, [::]) through the loopback.
    private static Uri Reachable(Uri address)
    {
        var builder = new UriBuilder(address);
        if (IPAddress.TryParse(builder.Host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
            builder.Host = ip.Equals(IPAddress.Any) ? "127.0.0.1" : "[::1]";
        return builder.Uri;
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
        var code = await WriteStatusAsync(paths!).ConfigureAwait(false);
        await WriteServiceAsync(options, paths!).ConfigureAwait(false);
        return code;
    }

    // Whether the service of the data root is installed, and whether the server will start again after a restart.
    private async Task WriteServiceAsync(DataRootOptions options, ServerPaths paths)
    {
        if ((ServiceEnvironment ?? Fleet.Update.Service.ServiceEnvironment.Current)() is not { } environment)
        {
            await _output.WriteLineAsync("Service: altaserver install does not know the service manager of this OS.").ConfigureAwait(false);
            return;
        }

        var manager = new ServiceManager(environment);
        try
        {
            var executable = (ServiceExecutable ?? LocateExecutable)() ?? new ServiceExecutable(ServiceExecutableKind.Executable, UpdateProduct.Server.AssemblyName, []);
            var plan = manager.Plan(ServiceRequestOf(new InstallOptions { DataRoot = options.DataRoot }, paths, ServiceScope.User, executable));
            // The system service of install --system first: the one a server started at boot runs under.
            // A Windows service of its own account keeps the default name and runs a data root under %ProgramData%: this one, when it names it.
            foreach (var line in ServiceManager.Describe(manager.QueryInstalled(plan, ServiceDefinition.ServiceName(UpdateProduct.Server, paths.DataRoot, true, developer: false),
                paths.DataRoot))) await _output.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (ServiceException exception)
        {
            await _output.WriteLineAsync("Service: " + exception.Message).ConfigureAwait(false);
        }
    }

    private async Task<int> WriteStatusAsync(ServerPaths paths)
    {
        if (!CoordinatorServer.IsRunning(paths))
        {
            await _output.WriteLineAsync($"No server runs on {paths.DataRoot}.").ConfigureAwait(false);
            await WriteVersionsAsync(paths, null).ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }

        var state = CoordinatorServer.ReadState(paths);
        if (state is null)
        {
            await _output.WriteLineAsync($"A server holds {paths.DataRoot} but has not written its state yet (it is starting).").ConfigureAwait(false);
            await WriteVersionsAsync(paths, null).ConfigureAwait(false);
            return ServerExitCodes.Success;
        }

        await WriteVersionsAsync(paths, state.Version).ConfigureAwait(false);

        await _output.WriteLineAsync($"CodeAlta Server {state.Version}, process {state.ProcessId}, since {state.StartedAt.ToLocalTime():g}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Listens on {string.Join(", ", state.Urls)}").ConfigureAwait(false);
        // The pin a device is given beside the https:// address: the key of the certificate it then trusts, alone.
        if (state.CertificatePin is { } pin) await _output.WriteLineAsync($"Certificate: {pin}").ConfigureAwait(false);
        var address = state.Urls.FirstOrDefault(static url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? state.Urls.FirstOrDefault();
        if (address is not null)
        {
            try
            {
                using var client = state.CertificatePin is { } pinned && address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? new HttpClient(CodeAlta.Fleet.Protocol.FleetServerCertificates.CreateHandler(pinned), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(3) }
                    : new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var health = await client.GetFromJsonAsync(new Uri(Reachable(new Uri(address)), "api/v1/server/health"), ServerApiJsonContext.Default.ServerHealthDto)
                    .ConfigureAwait(false);
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

    private async ValueTask<int> InstallAsync(InstallOptions options)
    {
        var (manager, plan, failure) = await PlanServiceAsync(options, "install").ConfigureAwait(false);
        if (manager is null || plan is null) return failure;
        if (options.DryRun)
        {
            await WritePlanAsync(plan, install: true).ConfigureAwait(false);
            return ServerExitCodes.Success;
        }

        options.TryResolve(_error, out var paths);
        var running = CoordinatorServer.IsRunning(paths!) ? CoordinatorServer.ReadState(paths!) : null;
        // The password goes to Windows only: read here, never printed, never written.
        // Read here, never printed, never written: typed at a prompt that does not show it, or the first line of a pipe.
        var password = options.PasswordStdin ? await ServicePassword.ReadAsync().ConfigureAwait(false) : null;
        var code = manager.Install(plan, _output, _error, password);
        if (code != ServiceManager.Success) return ServerExitCodes.Failure;
        await _output.WriteLineAsync(ServiceExecutableLocator.Describe(UpdateProduct.Server, (ServiceExecutable ?? LocateExecutable)()!)).ConfigureAwait(false);
        // The server of the service it replaced stopped with it (launchd, systemd): only a server that still holds the data
        // root keeps the new one waiting.
        if (running is not null && plan.Spec.Scope == ServiceScope.User && CoordinatorServer.IsRunning(paths!) && CoordinatorServer.ReadState(paths!)?.ProcessId == running.ProcessId)
        {
            await _output.WriteLineAsync($"A server already runs on {paths!.DataRoot} (process {running.ProcessId}): the service's server starts once it exits "
                + "(one server runs per data root).").ConfigureAwait(false);
        }

        await _output.WriteLineAsync("altaserver status tells whether it runs.").ConfigureAwait(false);
        return ServerExitCodes.Success;
    }

    private async ValueTask<int> UninstallAsync(InstallOptions options)
    {
        var (manager, plan, failure) = await PlanServiceAsync(options, "uninstall").ConfigureAwait(false);
        if (manager is null || plan is null) return failure;
        if (options.DryRun)
        {
            await WritePlanAsync(plan, install: false).ConfigureAwait(false);
            return ServerExitCodes.Success;
        }

        var installed = manager.Query(plan).Installed;
        if (manager.Uninstall(plan, _output, _error) != ServiceManager.Success) return ServerExitCodes.Failure;
        options.TryResolve(_error, out var paths);
        return installed && plan.LeavesProcessRunning ? await StopServerAsync(paths!).ConfigureAwait(false) : ServerExitCodes.Success;
    }

    // Asks the server of a data root to stop, without waiting: the file it watches.
    private static bool RequestStopThroughDataRoot(ServerPaths paths)
    {
        try
        {
            File.WriteAllText(paths.StopRequestPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Stops the server of a data root the service manager leaves running (the task at logon of Windows): it is asked through
    // its data root, and drains as on Ctrl+C. An instance started by hand, not by the service, is stopped too: it held the
    // data root the service was for.
    private async Task<int> StopServerAsync(ServerPaths paths)
    {
        if (!CoordinatorServer.IsRunning(paths)) return ServerExitCodes.Success;
        var state = CoordinatorServer.ReadState(paths);
        await _output.WriteLineAsync($"Stopping the server of this data root{(state is null ? "" : $" (process {state.ProcessId})")}...").ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(paths.StopRequestPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await _error.WriteLineAsync($"altaserver uninstall: cannot ask the server to stop ({exception.Message}).").ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }

        var deadline = DateTime.UtcNow + StopPatience;
        while (CoordinatorServer.IsRunning(paths))
        {
            if (DateTime.UtcNow > deadline)
            {
                TryDelete(paths.StopRequestPath);
                await _error.WriteLineAsync($"altaserver uninstall: the server still runs after {StopPatience.TotalSeconds:0} s.").ConfigureAwait(false);
                return ServerExitCodes.Failure;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }

        await _output.WriteLineAsync("The server stopped.").ConfigureAwait(false);
        return ServerExitCodes.Success;
    }

    // The manager and the plan of the service the options name, or the exit code of what was reported instead.
    private async Task<(ServiceManager? Manager, ServicePlan? Plan, int Failure)> PlanServiceAsync(InstallOptions options, string command)
    {
        if (!options.TryResolve(_error, out var paths)) return (null, null, ServerExitCodes.InvalidUsage);
        string? usage = null;
        if (options.User && options.System) usage = "--user and --system cannot go together.";
        // A Windows service runs as its own virtual account without one; launchd and systemd need the account.
        else if (options.System && options.Account is null && !OperatingSystem.IsWindows()) usage = "--system needs --account <name>, the account the server runs as.";
        else if (options.PasswordStdin && options.Account is null) usage = "--password-stdin goes with --account.";
        else if (!options.System && options.Account is not null) usage = "--account goes with --system only.";
        else if (options.Port is not null && (!int.TryParse(options.Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535))
            usage = $"--port needs a port from 1 to 65535: '{options.Port}'.";
        else if (options.TrustedProxies is { } proxies && !TryReadProxies(proxies, out var proxyProblem)) usage = proxyProblem;
        else if (options.Urls is { } urls)
        {
            // A service that would refuse to start is refused before it is installed.
            try { ServerBindings.Require(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), options.AllowInsecureHttp); }
            catch (ArgumentException exception) { usage = exception.Message; }
        }
        if (usage is not null)
        {
            await _error.WriteLineAsync("altaserver: " + usage).ConfigureAwait(false);
            return (null, null, ServerExitCodes.InvalidUsage);
        }

        if ((ServiceEnvironment ?? Fleet.Update.Service.ServiceEnvironment.Current)() is not { } environment)
        {
            await _error.WriteLineAsync($"altaserver {command}: this OS has no service manager altaserver knows (Windows, macOS and Linux with systemd).").ConfigureAwait(false);
            return (null, null, ServerExitCodes.Failure);
        }

        if ((ServiceExecutable ?? LocateExecutable)() is not { } executable)
        {
            await _error.WriteLineAsync($"altaserver {command}: the executable of this process cannot be found: run altaserver from its installed path.").ConfigureAwait(false);
            return (null, null, ServerExitCodes.Failure);
        }

        var manager = new ServiceManager(environment);
        try
        {
            return (manager, manager.Plan(ServiceRequestOf(options, paths!, options.System ? ServiceScope.System : ServiceScope.User, executable)), ServerExitCodes.Success);
        }
        catch (ServiceException exception)
        {
            await _error.WriteLineAsync($"altaserver {command}: {exception.Message}").ConfigureAwait(false);
            return (null, null, ServerExitCodes.Failure);
        }
    }

    /// <summary>The service the options of <c>install</c> ask for: <c>altaserver run</c> with the data root and the options of the server.</summary>
    internal static ServiceRequest ServiceRequestOf(InstallOptions options, ServerPaths paths, ServiceScope scope, ServiceExecutable executable)
    {
        var arguments = new List<string> { "run" };
        if (options.DataRoot is not null) arguments.AddRange(["--data-root", paths.DataRoot]);
        if (options.Port is { } port) arguments.AddRange(["--port", port]);
        if (options.Urls is { } urls) arguments.AddRange(["--urls", urls]);
        if (options.Certificate is { } certificate) arguments.AddRange(["--certificate", Path.GetFullPath(certificate)]);
        if (options.CertificatePasswordVariable is { } variable) arguments.AddRange(["--certificate-password-env", variable]);
        if (options.AllowInsecureHttp) arguments.Add(ServerBindings.AllowInsecureHttpOption);
        if (options.OwnCertificate) arguments.Add("--own-certificate");
        if (options.TrustedProxies is { } proxies) arguments.AddRange(["--trusted-proxy", proxies]);
        var system = scope == ServiceScope.System;
        return new ServiceRequest
        {
            Product = UpdateProduct.Server, Name = ServiceDefinition.ServiceName(UpdateProduct.Server, paths.DataRoot, options.DataRoot is null, developer: false),
            Scope = scope, Account = options.Account, Executable = executable, RunArguments = arguments,
            StateRoot = !system || options.DataRoot is not null ? paths.DataRoot : null, StateRootInHome = ".altaserver",
        };
    }

    private async Task WritePlanAsync(ServicePlan plan, bool install)
    {
        await _output.WriteLineAsync($"altaserver {(install ? "install" : "uninstall")} would {(install ? "install" : "remove")} {plan.Kind}: {plan.Location}.").ConfigureAwait(false);
        if (install)
        {
            await _output.WriteLineAsync().ConfigureAwait(false);
            await _output.WriteLineAsync($"{plan.DefinitionPath}{(plan.DefinitionIsTemporary ? " (given to the service manager, then deleted)" : "")}:").ConfigureAwait(false);
            await _output.WriteLineAsync(plan.Definition.TrimEnd()).ConfigureAwait(false);
            await _output.WriteLineAsync().ConfigureAwait(false);
        }

        await _output.WriteLineAsync("Commands:").ConfigureAwait(false);
        foreach (var command in install ? plan.Install : plan.Uninstall) await _output.WriteLineAsync("  " + command).ConfigureAwait(false);
        if (!install && !plan.DefinitionIsTemporary) await _output.WriteLineAsync($"  (then deletes {plan.DefinitionPath})").ConfigureAwait(false);
        if (!install && plan.LeavesProcessRunning) await _output.WriteLineAsync("  (then asks the server of the data root to stop)").ConfigureAwait(false);
        if (install && plan.LingerUser is { } user) await _output.WriteLineAsync($"  loginctl enable-linger {user} (when lingering is off)").ConfigureAwait(false);
        foreach (var note in plan.Notes) await _output.WriteLineAsync("Note: " + note).ConfigureAwait(false);
    }

    // The executable a service starts: the shim of the .NET tool, the app host of an archive, or dotnet with the assembly.
    private ServiceExecutable? LocateExecutable()
        => ServiceExecutableLocator.Locate(UpdateProduct.Server, Environment.ProcessPath, Path.Combine(ApplicationDirectory ?? AppContext.BaseDirectory, UpdateProduct.Server.AssemblyFileName),
            static path => File.Exists(path) || Directory.Exists(path));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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

    internal class DataRootOptions
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

    internal class RunOptions : DataRootOptions
    {
        public string? Port { get; set; }

        public string? Urls { get; set; }

        public string? Certificate { get; set; }

        public string? CertificatePasswordVariable { get; set; }

        public bool AllowInsecureHttp { get; set; }

        public bool OwnCertificate { get; set; }

        public string? TrustedProxies { get; set; }

        public bool Service { get; set; }
    }

    private static string[] Proxies(string? text) => text is null ? [] : text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryReadProxies(string text, out string? problem)
    {
        try
        {
            // The addresses must read as the server reads them at its start.
            var proxies = new TrustedProxies(Proxies(text));
            problem = proxies.Any ? null : "--trusted-proxy names no address.";
            return problem is null;
        }
        catch (ArgumentException exception)
        {
            problem = exception.Message;
            return false;
        }
    }

    /// <summary>The options of <c>install</c> and <c>uninstall</c>: those of the <c>run</c> the service starts, and whom it runs as.</summary>
    internal sealed class InstallOptions : RunOptions
    {
        public bool User { get; set; }

        public bool System { get; set; }

        public string? Account { get; set; }

        public bool DryRun { get; set; }

        public bool PasswordStdin { get; set; }
    }

    private sealed class BackupsOptions : DataRootOptions
    {
        public bool DeleteUnsealed { get; set; }
    }

    private async ValueTask<int> BackupsAsync(BackupsOptions options)
    {
        if (!options.TryResolve(_error, out var paths)) return ServerExitCodes.InvalidUsage;
        var list = ServerBackups.List(paths!);
        if (list.Count == 0) await _output.WriteLineAsync($"No backup in {paths!.BackupsRoot}.").ConfigureAwait(false);
        foreach (var backup in list)
        {
            var state = !backup.Unsealed ? "no secret in clear" : backup.KeptForTrial is { } version ? $"secrets in clear, kept while {version} is on trial" : "secrets in clear";
            await _output.WriteLineAsync($"{Path.GetFileName(backup.Path)}: {state}").ConfigureAwait(false);
        }

        if (!options.DeleteUnsealed) return ServerExitCodes.Success;
        try
        {
            var (deleted, kept) = ServerBackups.DeleteUnsealed(paths!);
            foreach (var file in deleted) await _output.WriteLineAsync($"Deleted {Path.GetFileName(file)}.").ConfigureAwait(false);
            foreach (var backup in kept)
                await _output.WriteLineAsync($"Kept {Path.GetFileName(backup.Path)}: {backup.KeptForTrial} rolls back to it while on trial; run this again once it confirmed.").ConfigureAwait(false);
            return ServerExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CodeAlta.Fleet.Update.UpdateException)
        {
            await _error.WriteLineAsync($"altaserver backups: {exception.Message}").ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }
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
