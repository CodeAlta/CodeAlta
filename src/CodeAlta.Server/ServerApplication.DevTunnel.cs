using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Server.Hosting;
using XenoAtom.CommandLine;

namespace CodeAlta.Server;

public sealed partial class ServerApplication
{
    /// <summary>Gets the Dev Tunnel commands supplied by a test; null runs the installed command line.</summary>
    internal IServerDevTunnelCommands? DevTunnelCommands { get; init; }

    private Command TunnelCommand()
    {
        var configure = new TunnelConfigureOptions();
        var configureCommand = new Command("configure", "Create or reuse a private tunnel; sign in with a device code if needed")
        {
            { "port=", "The HTTP loopback {PORT} of the server (2586 by default)", value => configure.Port = value },
        };
        AddDataRoot(configureCommand, configure);
        configureCommand.Add((_, _) => TunnelAsync(configure, "configure"));
        var status = new DataRootOptions();
        var statusCommand = new Command("status", "Print the saved tunnel id and its public address");
        AddDataRoot(statusCommand, status);
        statusCommand.Add((_, _) => TunnelAsync(status, "status"));
        var token = new TunnelTokenOptions();
        var tokenCommand = new Command("token", "Print a secret connect-only credential as JSON; give it only to your device")
        {
            { "invitation", "Print a transport refresh code for an existing device link instead of JSON", value => token.Invitation = value is not null },
        };
        AddDataRoot(tokenCommand, token);
        tokenCommand.Add((_, _) => TunnelAsync(token, "token"));
        var host = new TunnelHostOptions();
        var hostCommand = new Command("host", "Host the saved tunnel in the foreground (used by its service)")
        {
            { "service", "Run under the Windows Service Control Manager (set by install)", value => host.Service = value is not null },
        };
        AddDataRoot(hostCommand, host);
        hostCommand.Add((_, _) => TunnelAsync(host, "host"));
        return new Command("tunnel", "Manage the persistent private Dev Tunnel of this server") { configureCommand, statusCommand, tokenCommand, hostCommand };
    }

    private async ValueTask<int> TunnelAsync(DataRootOptions options, string action)
    {
        if (!options.TryResolve(_error, out var paths)) return ServerExitCodes.InvalidUsage;
        try
        {
            var tunnel = Tunnel(paths!);
            if (action == "configure")
            {
                var text = ((TunnelConfigureOptions)options).Port ?? "2586";
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) return ServerExitCodes.InvalidUsage;
                var configured = await tunnel.ConfigureAsync(port, line => _output.WriteLine("Dev Tunnel sign-in: " + line), CancellationToken.None).ConfigureAwait(false);
                await _output.WriteLineAsync("Dev Tunnel: " + configured.TunnelId).ConfigureAwait(false);
            }
            else if (action == "token")
            {
                var access = await tunnel.IssueAsync(CancellationToken.None).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No Dev Tunnel is configured for this data root.");
                // This refresh code does not enroll a new device: only an existing link accepts it without enrollment.
                var value = ((TunnelTokenOptions)options).Invitation
                    ? "ca-tunnel:" + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new FleetTunnelInvitationDto("transport-refresh", access), ServerApiJsonContext.Default.FleetTunnelInvitationDto))
                    : JsonSerializer.Serialize(access, ServerApiJsonContext.Default.FleetTunnelAccessDto);
                await _output.WriteLineAsync(value).ConfigureAwait(false);
            }
            else if (action == "status")
            {
                var saved = tunnel.Read() ?? throw new InvalidOperationException("No Dev Tunnel is configured for this data root.");
                await _output.WriteLineAsync("Dev Tunnel: " + saved.TunnelId).ConfigureAwait(false);
                if (saved.Address is { } address) await _output.WriteLineAsync("Address: " + address).ConfigureAwait(false);
            }
            else
            {
                if (((TunnelHostOptions)options).Service)
                {
                    if (!OperatingSystem.IsWindows()) return ServerExitCodes.InvalidUsage;
                    return WindowsServiceHost.Run(ServiceDefinition.Noun(UpdateProduct.Server), token => RunTunnelHostAsync(tunnel, token));
                }
                return await RunTunnelHostAsync(tunnel, CancellationToken.None).ConfigureAwait(false);
            }

            return ServerExitCodes.Success;
        }
        catch (Exception exception) when (TunnelFailure(exception))
        {
            await _error.WriteLineAsync("altaserver tunnel: " + TunnelProblem(exception)).ConfigureAwait(false);
            return ServerExitCodes.Failure;
        }
    }

    private ServerDevTunnel Tunnel(ServerPaths paths)
    {
        if (DevTunnelCommands is { } commands) return new(paths, commands);
        var initial = new ServerDevTunnel(paths, new ServerDevTunnelCommands());
        return initial.Read() is { Executable: { Length: > 0 } executable } ? new(paths, new ServerDevTunnelCommands(executable)) : initial;
    }

    private static bool ConfigureTunnelBindings(InstallOptions options, out string? problem)
    {
        problem = null;
        if (!int.TryParse(options.Port ?? "2586", NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            problem = "--port needs a port from 1 to 65535.";
        else if (OperatingSystem.IsWindows() && options.System && options.Account is null)
            problem = "A Dev Tunnel needs the owner's credential cache: use a user service, or --system --account with a tunnel configured as that account first.";
        else if (options.OwnCertificate || options.Certificate is not null || options.AllowInsecureHttp)
            problem = "A Dev Tunnel forwards HTTP to loopback; do not combine --dev-tunnel with a certificate or --allow-insecure-http.";
        else if (options.Urls is { } urls && urls != $"http://127.0.0.1:{port}")
            problem = "--dev-tunnel serves only http://127.0.0.1:<port>.";
        else if (options.TrustedProxies is { } proxies && proxies != "127.0.0.1;::1")
            problem = "--dev-tunnel trusts only its loopback proxy (127.0.0.1;::1).";
        if (problem is not null) return false;
        options.Urls = $"http://127.0.0.1:{port}";
        options.TrustedProxies = "127.0.0.1;::1";
        return true;
    }

    // Same executable/account/environment as the server, a distinct service and a distinct process lifetime.
    internal static ServiceRequest TunnelServiceRequest(ServiceRequest server, ServerPaths paths)
        => server with { Name = server.Name + "-tunnel", RunArguments = ["tunnel", "host", "--data-root", paths.DataRoot] };

    private static bool TunnelFailure(Exception exception) => exception is InvalidOperationException or IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException;

    private static string TunnelProblem(Exception exception) => exception is OperationCanceledException or TimeoutException
        ? "The Dev Tunnel command did not finish in time. Check devtunnel and sign in again as the service account."
        : exception is System.ComponentModel.Win32Exception ? "The Dev Tunnel command line could not start." : exception.Message;

    private async Task<int> RunTunnelHostAsync(ServerDevTunnel tunnel, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        try
        {
            var code = await tunnel.HostAsync(address => _output.WriteLine("Dev Tunnel address: " + address), stop.Token).ConfigureAwait(false);
            if (code != 0) await _error.WriteLineAsync("Dev Tunnel host stopped. Check the service account's login, private access and usage limits.").ConfigureAwait(false);
            return code;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private async Task<bool> StopTunnelAsync(ServerDevTunnel tunnel)
    {
        CodeAlta.Core.IO.SafeFile.WriteAllText(tunnel.StopPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        var deadline = DateTimeOffset.UtcNow + StopPatience;
        while (File.Exists(tunnel.HostLockPath))
        {
            if (CodeAlta.Core.IO.LockFile.TryAcquire(tunnel.HostLockPath, out var held)) { held.Dispose(); return true; }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                await _error.WriteLineAsync("altaserver uninstall: the Dev Tunnel host did not stop. Its service was removed; check its process on this machine.").ConfigureAwait(false);
                return false;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        return true;
    }

    private sealed class TunnelConfigureOptions : DataRootOptions
    {
        public string? Port { get; set; }
    }

    private sealed class TunnelTokenOptions : DataRootOptions
    {
        public bool Invitation { get; set; }
    }

    private sealed class TunnelHostOptions : DataRootOptions
    {
        public bool Service { get; set; }
    }
}
