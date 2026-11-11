using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>What the person asks: CodeAlta Server on a computer reached over SSH, and how the others reach it.</summary>
internal sealed record SshServerSetupRequest
{
    /// <summary>Gets the computer.</summary>
    public required SshTarget Target { get; init; }

    /// <summary>Gets the port of the server (2586 by default).</summary>
    public int Port { get; init; } = 2586;

    /// <summary>
    /// Gets the address the desktop and the machines reach the server at; null for <c>http://&lt;host&gt;:&lt;port&gt;</c> on the local
    /// network, where the server then listens on every interface in plain HTTP, which the person chose (the server is
    /// started with <c>--allow-insecure-http</c>). With an address (a domain, a Tailscale name), the server stays on its
    /// loopback, behind what the person put in front of it: the default of the dialog.
    /// </summary>
    public Uri? Address { get; init; }

    /// <summary>
    /// Gets whether the server on the local network (no <see cref="Address"/>) is reached over HTTPS with a certificate of
    /// its own (<c>altaserver run --own-certificate</c>), whose pin the setup reads over SSH and every device then checks;
    /// otherwise over plain HTTP.
    /// </summary>
    public bool OwnCertificate { get; init; }

    /// <summary>Gets the data folder of the server there; null for <c>~/.altaserver</c>.</summary>
    public string? DataRoot { get; init; }

    /// <summary>Gets the folder of the tool there; null for <c>~/.dotnet/tools</c>.</summary>
    public string? ToolPath { get; init; }

    /// <summary>Gets a folder of this computer with the packages of CodeAlta Server; null for nuget.org.</summary>
    public string? PackageFolder { get; init; }

    /// <summary>Gets the fingerprint of the host key the person trusted, as it was shown to them: only that key is accepted; null for the keys ssh knows.</summary>
    public string? TrustedHostKey { get; init; }

    /// <summary>
    /// Gets whether the server starts when the computer starts, with nobody logged in (<b>Always on</b>, which the dialog
    /// ticks by default: the machines depend on it): a LaunchDaemon run as the person on macOS, lingering on Linux.
    /// </summary>
    public bool AlwaysOn { get; init; }

    /// <summary>
    /// Gets the password <c>sudo</c> asks there for <see cref="AlwaysOn"/> or <see cref="TailscaleServe"/>; given to
    /// <c>sudo -S</c> only, never kept. Null for none.
    /// </summary>
    public string? SudoPassword { get; init; }

    /// <summary>
    /// Gets whether the person asked that Tailscale Serve publish the server there, when <see cref="Address"/> is the name
    /// of the computer on its tailnet and nothing serves it yet. Without it, the setup only says what is missing, with the
    /// command.
    /// </summary>
    public bool TailscaleServe { get; init; }

    /// <summary>Gets whether the person asked the matching private-network provider to publish the server.</summary>
    public bool PublishNetwork { get; init; }

    /// <summary>Gets whether to create and host a persistent private Dev Tunnel on the server's machine.</summary>
    public bool DevTunnel { get; init; }
}

/// <summary>The steps of a server set up over SSH.</summary>
internal static class SshServerSteps
{
    public const string Reach = "reach";
    public const string Claim = "claim";
}

/// <summary>
/// Sets up CodeAlta Server on a computer reached over SSH, as its owner would there: connect and probe, install the
/// tool, run it as a service of the user with <c>altaserver install</c> (a <c>systemd --user</c> unit with lingering, or a
/// LaunchAgent), check from this
/// computer that it answers at the address the machines will use, then read its one-time setup code over SSH and claim
/// it through a port forwarded to its loopback — the setup of a server is accepted from its own machine only — so that
/// nothing is copied by hand.
/// </summary>
internal sealed class SshServerInstaller(ISshRunner ssh, string desktopVersion, PrivateNetworkRegistry? privateNetworks = null)
{
    /// <summary>The package of the tool.</summary>
    internal const string PackageId = "CodeAlta.Server";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The proxies a server behind an address of its own trusts: the ones on its own loopback (<c>altaserver --trusted-proxy</c>).</summary>
    internal const string LoopbackProxies = "127.0.0.1;::1";

    /// <summary>Gets what keeps a tunnel credential read over SSH before checking the public address. It is never progress text.</summary>
    internal Func<FleetTunnelAccessDto, CancellationToken, Task>? TunnelAccessReceived { get; init; }

    /// <summary>Runs the setup.</summary>
    /// <param name="request">The computer and the options.</param>
    /// <param name="reachable">Checks that the server answers at an address from this computer, with the pin of its own certificate if it has one: null when it does, otherwise what blocked it.</param>
    /// <param name="claim">Makes this computer the owner: the address of the link, the address of the setup (the forward), the code, the pin of its own certificate or null; returns the id of the link.</param>
    /// <param name="progress">Told where each step stands.</param>
    /// <param name="cancellationToken">Stops the setup.</param>
    /// <returns>The id of the link of this computer to the new server.</returns>
    /// <exception cref="SshSetupException">A step failed; its code says why.</exception>
    public async Task<string> RunAsync(SshServerSetupRequest request, Func<Uri, string?, CancellationToken, Task<ReachProblem?>> reachable,
        Func<Uri, Uri, string, string?, CancellationToken, Task<string>> claim, Action<SshSetupProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        var onNetwork = request.Address is null && !request.DevTunnel;
        var encrypted = onNetwork && request.OwnCertificate;
        var address = request.Address ?? new Uri($"{(encrypted ? "https" : "http")}://{(target.Host.Contains(':', StringComparison.Ordinal) ? $"[{target.Host}]" : target.Host)}:"
            + request.Port.ToString(CultureInfo.InvariantCulture));

        progress(new(SshSetupSteps.Connect, "running", target.Destination));
        SshResult probe;
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(45));
            try { probe = await ssh.RunAsync(target, SshNodeInstaller.ProbeScript, request.TrustedHostKey, null, connecting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SshSetupException(SshSetupSteps.Connect, "unreachable", "No answer within 45 s."); }
        }

        if (probe.ExitCode == SshResult.ConnectionFailed) throw await SshNodeInstaller.ConnectionFailureAsync(ssh, target, probe.Error, cancellationToken).ConfigureAwait(false);
        var facts = SshNodeInstaller.ReadFacts(probe.Output);
        progress(new(SshSetupSteps.Connect, "done", $"{facts.User}@{facts.Hostname ?? target.Host}"));

        progress(new(SshSetupSteps.Check, "running"));
        if (facts.RuntimeIdentifier is not { } rid || facts.Os is not ("Linux" or "Darwin")) throw new SshSetupException(SshSetupSteps.Check, "unsupported_system", $"{facts.OsName} {facts.Architecture}");
        if (facts.Dotnet is null || facts.Sdk is null) throw new SshSetupException(SshSetupSteps.Check, "dotnet_missing", $"{facts.OsName} {facts.Architecture}");
        if (request.DevTunnel && !facts.DevTunnel)
            throw new SshSetupException(SshSetupSteps.Check, "devtunnel_not_installed", "https://learn.microsoft.com/azure/developer/dev-tunnels/get-started");
        SshNodeInstaller.CheckAlwaysOn(facts, request.AlwaysOn, request.SudoPassword, "altaserver install");
        progress(new(SshSetupSteps.Check, "done", $"{facts.OsName} {facts.Architecture} · .NET SDK {facts.Sdk}"));

        progress(new(SshSetupSteps.Install, "running"));
        var toolPath = request.ToolPath ?? "~/.dotnet/tools";
        var dataRoot = request.DataRoot ?? "~/.altaserver";
        var packages = request.PackageFolder is { Length: > 0 } folder ? SshNodeInstaller.LocalPackages(folder, rid, PackageId) : null;
        var version = packages?.Version ?? SshNodeInstaller.ReleaseVersion(desktopVersion);
        string? source = null;
        if (packages is not null)
        {
            var stage = await ssh.RunAsync(target, "set -e\nd=\"$HOME/.cache/codealta-setup\"\nrm -rf \"$d\"\nmkdir -p \"$d\"\nprintf '%s\\n' \"$d\"\n", request.TrustedHostKey, null, cancellationToken)
                .ConfigureAwait(false);
            source = stage.Output.Trim();
            if (!stage.Succeeded || source.Length == 0) throw new SshSetupException(SshSetupSteps.Install, "copy_failed", Lines(stage.Error));
            var copied = await ssh.CopyAsync(target, packages.Files, source, request.TrustedHostKey, cancellationToken).ConfigureAwait(false);
            if (!copied.Succeeded) throw new SshSetupException(SshSetupSteps.Install, "copy_failed", Lines(copied.Error));
        }

        var install = await ssh.RunAsync(target, InstallScript(facts, toolPath, version, source), request.TrustedHostKey, null, cancellationToken).ConfigureAwait(false);
        if (!install.Succeeded) throw new SshSetupException(SshSetupSteps.Install, "tool_install_failed", Lines(install.Error + install.Output));
        progress(new(SshSetupSteps.Install, "done", $"CodeAlta Server {version} · {toolPath}"));

        progress(new(SshSetupSteps.Service, "running"));
        if (request.DevTunnel)
        {
            // Run as the SSH account, before a system service's sudo: its credential cache must own the tunnel.
            var configure = await ssh.RunAsync(target, TunnelScript(facts, toolPath, dataRoot, $"configure --port {request.Port.ToString(CultureInfo.InvariantCulture)}"),
                request.TrustedHostKey, line =>
                {
                    if (line.StartsWith("Dev Tunnel sign-in:", StringComparison.Ordinal)) progress(new(SshSetupSteps.Service, "running", line));
                }, cancellationToken).ConfigureAwait(false);
            if (!configure.Succeeded) throw new SshSetupException(SshSetupSteps.Service, "devtunnel_setup_failed", Lines(configure.Error));
        }
        var service = await ssh.RunAsync(target, ServiceScript(facts, toolPath, request.DataRoot, request.Port, onNetwork, encrypted, request.AlwaysOn, request.SudoPassword, request.DevTunnel),
            request.TrustedHostKey, null, cancellationToken).ConfigureAwait(false);
        if (!service.Succeeded)
            throw new SshSetupException(SshSetupSteps.Service, SshNodeInstaller.SudoRefused(service.Error + service.Output) ? "sudo_refused" : "service_failed", Lines(service.Error + service.Output));
        // The pin of the certificate the server made, as its status printed it over SSH: the channel this computer trusts
        // already, so that no one on the network can put a certificate of their own in its place.
        var marker = service.Output.IndexOf(SshNodeInstaller.StatusMarker, StringComparison.Ordinal);
        var pin = encrypted ? (marker < 0 ? service.Output : service.Output[..marker]).Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(FleetServerCertificates.IsPin) : null;
        if (encrypted && pin is null) throw new SshSetupException(SshSetupSteps.Service, "service_failed", "The server did not print the pin of its certificate. " + Lines(service.Output));
        if (request.DevTunnel)
        {
            SshResult credential;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(StartTimeout);
            try
            {
                credential = await ssh.RunAsync(target, TunnelScript(facts, toolPath, dataRoot, "token", waitForHost: true),
                    request.TrustedHostKey, null, waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SshSetupException(SshSetupSteps.Service, "devtunnel_setup_failed", "The tunnel service did not report its address within 60 s.");
            }

            FleetTunnelAccessDto? access = null;
            try { if (credential.Succeeded) access = JsonSerializer.Deserialize(credential.Output.Trim(), ServerApiJsonContext.Default.FleetTunnelAccessDto); }
            catch (JsonException) { }
            if (access is null || !Uri.TryCreate(access.Address, UriKind.Absolute, out var tunnelAddress) || !FleetTunnelAccessStore.IsTunnel(tunnelAddress))
                throw new SshSetupException(SshSetupSteps.Service, "devtunnel_setup_failed", "The tunnel service did not return a private HTTPS address and a connect credential. Check altaserver tunnel status on the machine.");
            address = tunnelAddress;
            if (TunnelAccessReceived is { } received) await received(access, cancellationToken).ConfigureAwait(false);
        }
        var restart = SshNodeInstaller.AfterRestart(service.Output);
        progress(new(SshSetupSteps.Service, "done", (facts.Os == "Linux" ? "systemd" : "launchd") + $", port {request.Port}"
            + (onNetwork ? ", every network of the machine" : ", its loopback") + (encrypted ? ", HTTPS with its own certificate" : "") + (restart is null ? "" : $" · {restart}")));

        progress(new(SshServerSteps.Reach, "running", address.ToString().TrimEnd('/')));
        var blocked = await reachable(address, pin, cancellationToken).ConfigureAwait(false);
        if (blocked is { BlockedHere: false } && !onNetwork
            && (privateNetworks ?? new PrivateNetworkRegistry(static () => [])).FindForAddress(address) is { } network)
        {
            var diagnosis = await network.CheckRemoteAsync(new SshPrivateNetworkHost(ssh, request.Target, request.TrustedHostKey, facts, request.SudoPassword),
                address, request.Port, request.PublishNetwork || request.TailscaleServe, cancellationToken).ConfigureAwait(false);
            if (diagnosis?.ProblemCode is { } problemCode) throw new SshSetupException(SshServerSteps.Reach, problemCode, diagnosis.Detail);
            if (diagnosis is not null)
            {
                if (diagnosis.Recheck)
                {
                    progress(new(SshServerSteps.Reach, "running", $"{address.ToString().TrimEnd('/')} · {diagnosis.Activity ?? network.DisplayName}"));
                    blocked = await reachable(address, pin, cancellationToken).ConfigureAwait(false);
                }

                if (blocked is not null) blocked = new ReachProblem($"{blocked.Reason} · {diagnosis.Detail}", blocked.BlockedHere);
            }
        }
        if (blocked is not null)
            throw new SshSetupException(SshServerSteps.Reach, blocked.BlockedHere ? "local_network_blocked"
                : onNetwork ? facts.Firewall == "ufw" ? "server_blocked_ufw" : "server_blocked" : "server_proxy_unreachable",
                $"{address.ToString().TrimEnd('/')} · {blocked.Reason}");
        progress(new(SshServerSteps.Reach, "done", address.ToString().TrimEnd('/')));

        progress(new(SshServerSteps.Claim, "running"));
        var code = await ssh.RunAsync(target, SetupCodeScript(facts, toolPath, dataRoot), request.TrustedHostKey, null, cancellationToken).ConfigureAwait(false);
        if (!code.Succeeded)
            throw new SshSetupException(SshServerSteps.Claim, code.Error.Contains("already has an owner", StringComparison.OrdinalIgnoreCase) ? "server_owned" : "setup_code_failed", Lines(code.Error));
        var setupCode = code.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        SshForward forward;
        try { forward = await ssh.ForwardAsync(target, request.Port, request.TrustedHostKey, cancellationToken).ConfigureAwait(false); }
        catch (IOException exception) { throw new SshSetupException(SshServerSteps.Claim, "forward_failed", exception.Message); }
        using (forward)
        {
            var linkId = await claim(address, new Uri($"{(encrypted ? "https" : "http")}://127.0.0.1:{forward.LocalPort.ToString(CultureInfo.InvariantCulture)}"), setupCode, pin,
                cancellationToken).ConfigureAwait(false);
            progress(new(SshServerSteps.Claim, "done"));
            return linkId;
        }
    }

    private static string InstallScript(SshMachineFacts facts, string toolPath, string version, string? source)
    {
        var script = new StringBuilder(SshNodeInstaller.DotnetPrelude(facts));
        script.Append("TOOLS=").Append(SshNodeInstaller.Quote(toolPath)).Append("\nmkdir -p \"$TOOLS\"\n");
        script.Append("\"$DOTNET\" tool update --tool-path \"$TOOLS\" ").Append(PackageId).Append(" --version ").Append(SshNodeInstaller.Quote(version)).Append(" --allow-downgrade");
        if (source is not null) script.Append(" --add-source ").Append(SshNodeInstaller.Quote(source));
        script.Append(" 1>&2\n\"$TOOLS/altaserver\" --version\n");
        return script.ToString();
    }

    // The service of the user that runs altaserver run for the data root, as altaserver install writes it there (a
    // systemd --user unit with lingering, a LaunchAgent), with the PATH and the DOTNET_ROOT of the prelude; then waits until
    // the server answers its own status. The default data root has the service of ~/.altaserver; another one a service
    // named after it, so that two never collide.
    internal static string ServiceScript(SshMachineFacts facts, string toolPath, string? dataRoot, int port, bool onNetwork, bool encrypted, bool alwaysOn = false,
        string? sudoPassword = null, bool devTunnel = false)
    {
        var script = new StringBuilder(SshNodeInstaller.DotnetPrelude(facts));
        script.Append("TOOLS=").Append(SshNodeInstaller.Quote(toolPath)).Append("\nTOOLS=$(cd \"$TOOLS\" && pwd)\n");
        script.Append("ROOT=").Append(SshNodeInstaller.Quote(dataRoot ?? "~/.altaserver")).Append("\nmkdir -p \"$ROOT\"\nROOT=$(cd \"$ROOT\" && pwd)\n");
        // The service an earlier CodeAlta Desktop wrote itself for this data root goes first: two would contend for it.
        script.Append("ID=$(printf %s \"$ROOT\" | cksum | cut -d' ' -f1)\n");
        if (facts.Os == "Linux")
        {
            script.Append("OLD=\"$HOME/.config/systemd/user/altaserver-$ID.service\"\n");
            script.Append("if grep -qs 'Written by CodeAlta Desktop' \"$OLD\"; then systemctl --user disable --now \"altaserver-$ID.service\" 1>&2 || true; rm -f \"$OLD\"; systemctl --user daemon-reload; fi\n");
        }
        else
        {
            script.Append("OLD=\"$HOME/Library/LaunchAgents/io.codealta.altaserver-$ID.plist\"\n");
            script.Append("if [ -f \"$OLD\" ]; then launchctl bootout \"gui/$(id -u)/io.codealta.altaserver-$ID\" 2>/dev/null || true; rm -f \"$OLD\"; fi\n");
        }

        script.Append("ME=$(id -un)\n");
        var root = dataRoot is not null || devTunnel ? " --data-root \"$ROOT\"" : "";
        var sudo = SshNodeInstaller.Sudo(facts, sudoPassword);
        if (alwaysOn && facts.Os == "Darwin")
        {
            // One service per data root: the LaunchAgent goes, and a LaunchDaemon runs the person's tool as them.
            script.Append("\"$TOOLS/altaserver\" uninstall").Append(root).Append(" >/dev/null 2>&1 || true\n");
            script.Append(sudo).Append("env \"DOTNET_ROOT=$DOTNET_ROOT\" \"PATH=$PATH\" \"$TOOLS/altaserver\" install --system --account \"$ME\"");
        }
        else
        {
            script.Append("\"$TOOLS/altaserver\" install");
        }

        script.Append(root);
        if (devTunnel) script.Append(" --dev-tunnel --port ").Append(port.ToString(CultureInfo.InvariantCulture));
        // The local network is HTTPS with the server's own certificate, or plain HTTP that the person chose: the server
        // refuses that one unless it is forced.
        // Behind an address of its own, the server stays on its loopback, and what stands in front of it there (Tailscale
        // Serve, Caddy) is trusted for the caller and the scheme it forwards: the passkey page is then at its https:// address.
        script.Append(" --urls ").Append(!onNetwork ? $"http://127.0.0.1:{port} --trusted-proxy {SshNodeInstaller.Quote(LoopbackProxies)}"
                : encrypted ? $"https://0.0.0.0:{port} --own-certificate" : $"http://0.0.0.0:{port} --allow-insecure-http")
            .Append(" 1>&2\n");
        if (alwaysOn && facts.Os == "Linux")
            script.Append("if [ \"$(loginctl show-user \"$ME\" -p Linger --value 2>/dev/null)\" != yes ]; then ").Append(sudo).Append("loginctl enable-linger \"$ME\"; fi\n");

        script.Append("i=0\nwhile [ $i -lt ").Append((int)StartTimeout.TotalSeconds).Append(" ]; do\n");
        // "Health: ok" once it serves: status also prints "Health: no answer ..." while it starts.
        // Once it serves, the pin of its own certificate, on a line of its own (status prints "Certificate: sha256:...").
        script.Append("  if \"$TOOLS/altaserver\" status --data-root \"$ROOT\" 2>/dev/null | grep -q '^Health: ok'; then ")
            .Append(encrypted ? "\"$TOOLS/altaserver\" status --data-root \"$ROOT\" | sed -n 's/^Certificate: //p'; " : "")
            .Append("echo '").Append(SshNodeInstaller.StatusMarker).Append("'; \"$TOOLS/altaserver\" status --data-root \"$ROOT\" 2>/dev/null || true; exit 0; fi\n  i=$((i+1)); sleep 1\ndone\n");
        script.Append("\"$TOOLS/altaserver\" status --data-root \"$ROOT\" >&2\nexit 1\n");
        return script.ToString();
    }

    private static string SetupCodeScript(SshMachineFacts facts, string toolPath, string dataRoot)
    {
        var script = new StringBuilder(SshNodeInstaller.DotnetPrelude(facts));
        script.Append("TOOLS=").Append(SshNodeInstaller.Quote(toolPath)).Append('\n');
        script.Append("ROOT=").Append(SshNodeInstaller.Quote(dataRoot)).Append("\nROOT=$(cd \"$ROOT\" && pwd)\n");
        script.Append("exec \"$TOOLS/altaserver\" setup-code --data-root \"$ROOT\"\n");
        return script.ToString();
    }

    private static string TunnelScript(SshMachineFacts facts, string toolPath, string dataRoot, string action, bool waitForHost = false)
    {
        var script = new StringBuilder(SshNodeInstaller.DotnetPrelude(facts));
        script.Append("TOOLS=").Append(SshNodeInstaller.Quote(toolPath)).Append("\nROOT=").Append(SshNodeInstaller.Quote(dataRoot))
            .Append("\nmkdir -p \"$ROOT\"\nROOT=$(cd \"$ROOT\" && pwd)\n");
        if (waitForHost)
            script.Append("i=0\nwhile [ $i -lt 60 ]; do\n  OUT=$(\"$TOOLS/altaserver\" tunnel token --data-root \"$ROOT\" 2>/dev/null) && { printf '%s\\n' \"$OUT\"; exit 0; }\n  i=$((i+1)); sleep 1\ndone\nexit 1\n");
        else script.Append("\"$TOOLS/altaserver\" tunnel ").Append(action).Append(" --data-root \"$ROOT\"\n");
        return script.ToString();
    }

    // What ssh and the commands said, without the notice of a host key it just recorded, at most six lines.
    internal static string Lines(string text) => string.Join('\n', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(static line => !line.StartsWith("Warning: Permanently added", StringComparison.Ordinal)).Take(6));
}
