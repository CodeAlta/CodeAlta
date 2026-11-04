using System.Globalization;
using System.Text;

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
    /// network, where the server then listens on every interface. With an address (a domain, a Tailscale name), the
    /// server stays on its loopback, behind what the person put in front of it.
    /// </summary>
    public Uri? Address { get; init; }

    /// <summary>Gets the data folder of the server there; null for <c>~/.altaserver</c>.</summary>
    public string? DataRoot { get; init; }

    /// <summary>Gets the folder of the tool there; null for <c>~/.dotnet/tools</c>.</summary>
    public string? ToolPath { get; init; }

    /// <summary>Gets a folder of this computer with the packages of CodeAlta Server; null for nuget.org.</summary>
    public string? PackageFolder { get; init; }

    /// <summary>Gets whether a host key ssh does not know yet is accepted.</summary>
    public bool AcceptNewHostKey { get; init; }
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
internal sealed class SshServerInstaller(ISshRunner ssh, string desktopVersion)
{
    /// <summary>The package of the tool.</summary>
    internal const string PackageId = "CodeAlta.Server";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Runs the setup.</summary>
    /// <param name="request">The computer and the options.</param>
    /// <param name="reachable">Checks that the server answers at an address from this computer: null when it does, otherwise what blocked it.</param>
    /// <param name="claim">Makes this computer the owner: the address of the link, the address of the setup (the forward), the code; returns the id of the link.</param>
    /// <param name="progress">Told where each step stands.</param>
    /// <param name="cancellationToken">Stops the setup.</param>
    /// <returns>The id of the link of this computer to the new server.</returns>
    /// <exception cref="SshSetupException">A step failed; its code says why.</exception>
    public async Task<string> RunAsync(SshServerSetupRequest request, Func<Uri, CancellationToken, Task<ReachProblem?>> reachable,
        Func<Uri, Uri, string, CancellationToken, Task<string>> claim, Action<SshSetupProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Target;
        var address = request.Address ?? new Uri($"http://{(target.Host.Contains(':', StringComparison.Ordinal) ? $"[{target.Host}]" : target.Host)}:{request.Port.ToString(CultureInfo.InvariantCulture)}");
        var onNetwork = request.Address is null;

        progress(new(SshSetupSteps.Connect, "running", target.Destination));
        SshResult probe;
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(45));
            try { probe = await ssh.RunAsync(target, SshNodeInstaller.ProbeScript, request.AcceptNewHostKey, null, connecting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SshSetupException(SshSetupSteps.Connect, "unreachable", "No answer within 45 s."); }
        }

        if (probe.ExitCode == SshResult.ConnectionFailed) throw await SshNodeInstaller.ConnectionFailureAsync(ssh, target, probe.Error, cancellationToken).ConfigureAwait(false);
        var facts = SshNodeInstaller.ReadFacts(probe.Output);
        progress(new(SshSetupSteps.Connect, "done", $"{facts.User}@{facts.Hostname ?? target.Host}"));

        progress(new(SshSetupSteps.Check, "running"));
        if (facts.RuntimeIdentifier is not { } rid || facts.Os is not ("Linux" or "Darwin")) throw new SshSetupException(SshSetupSteps.Check, "unsupported_system", $"{facts.OsName} {facts.Architecture}");
        if (facts.Dotnet is null || facts.Sdk is null) throw new SshSetupException(SshSetupSteps.Check, "dotnet_missing", $"{facts.OsName} {facts.Architecture}");
        progress(new(SshSetupSteps.Check, "done", $"{facts.OsName} {facts.Architecture} · .NET SDK {facts.Sdk}"));

        progress(new(SshSetupSteps.Install, "running"));
        var toolPath = request.ToolPath ?? "~/.dotnet/tools";
        var dataRoot = request.DataRoot ?? "~/.altaserver";
        var packages = request.PackageFolder is { Length: > 0 } folder ? SshNodeInstaller.LocalPackages(folder, rid, PackageId) : null;
        var version = packages?.Version ?? SshNodeInstaller.ReleaseVersion(desktopVersion);
        string? source = null;
        if (packages is not null)
        {
            var stage = await ssh.RunAsync(target, "set -e\nd=\"$HOME/.cache/codealta-setup\"\nrm -rf \"$d\"\nmkdir -p \"$d\"\nprintf '%s\\n' \"$d\"\n", request.AcceptNewHostKey, null, cancellationToken)
                .ConfigureAwait(false);
            source = stage.Output.Trim();
            if (!stage.Succeeded || source.Length == 0) throw new SshSetupException(SshSetupSteps.Install, "copy_failed", Lines(stage.Error));
            var copied = await ssh.CopyAsync(target, packages.Files, source, request.AcceptNewHostKey, cancellationToken).ConfigureAwait(false);
            if (!copied.Succeeded) throw new SshSetupException(SshSetupSteps.Install, "copy_failed", Lines(copied.Error));
        }

        var install = await ssh.RunAsync(target, InstallScript(facts, toolPath, version, source), request.AcceptNewHostKey, null, cancellationToken).ConfigureAwait(false);
        if (!install.Succeeded) throw new SshSetupException(SshSetupSteps.Install, "tool_install_failed", Lines(install.Error + install.Output));
        progress(new(SshSetupSteps.Install, "done", $"CodeAlta Server {version} · {toolPath}"));

        progress(new(SshSetupSteps.Service, "running"));
        var service = await ssh.RunAsync(target, ServiceScript(facts, toolPath, request.DataRoot, request.Port, onNetwork), request.AcceptNewHostKey, null, cancellationToken)
            .ConfigureAwait(false);
        if (!service.Succeeded) throw new SshSetupException(SshSetupSteps.Service, "service_failed", Lines(service.Error + service.Output));
        progress(new(SshSetupSteps.Service, "done", (facts.Os == "Linux" ? "systemd --user unit" : "LaunchAgent") + $", port {request.Port}"
            + (onNetwork ? ", every network of the machine" : ", its loopback")));

        progress(new(SshServerSteps.Reach, "running", address.ToString().TrimEnd('/')));
        if (await reachable(address, cancellationToken).ConfigureAwait(false) is { } blocked)
            throw new SshSetupException(SshServerSteps.Reach, blocked.BlockedHere ? "local_network_blocked"
                : onNetwork ? facts.Firewall == "ufw" ? "server_blocked_ufw" : "server_blocked" : "server_proxy_unreachable",
                $"{address.ToString().TrimEnd('/')} · {blocked.Reason}");
        progress(new(SshServerSteps.Reach, "done", address.ToString().TrimEnd('/')));

        progress(new(SshServerSteps.Claim, "running"));
        var code = await ssh.RunAsync(target, SetupCodeScript(facts, toolPath, dataRoot), request.AcceptNewHostKey, null, cancellationToken).ConfigureAwait(false);
        if (!code.Succeeded)
            throw new SshSetupException(SshServerSteps.Claim, code.Error.Contains("already has an owner", StringComparison.OrdinalIgnoreCase) ? "server_owned" : "setup_code_failed", Lines(code.Error));
        var setupCode = code.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        SshForward forward;
        try { forward = await ssh.ForwardAsync(target, request.Port, request.AcceptNewHostKey, cancellationToken).ConfigureAwait(false); }
        catch (IOException exception) { throw new SshSetupException(SshServerSteps.Claim, "forward_failed", exception.Message); }
        using (forward)
        {
            var linkId = await claim(address, new Uri($"http://127.0.0.1:{forward.LocalPort.ToString(CultureInfo.InvariantCulture)}"), setupCode, cancellationToken).ConfigureAwait(false);
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
    private static string ServiceScript(SshMachineFacts facts, string toolPath, string? dataRoot, int port, bool onNetwork)
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

        script.Append("\"$TOOLS/altaserver\" install");
        if (dataRoot is not null) script.Append(" --data-root \"$ROOT\"");
        script.Append(" --urls ").Append(onNetwork ? $"http://0.0.0.0:{port}" : $"http://127.0.0.1:{port}").Append(" 1>&2\n");

        script.Append("i=0\nwhile [ $i -lt ").Append((int)StartTimeout.TotalSeconds).Append(" ]; do\n");
        // "Health: ok" once it serves: status also prints "Health: no answer ..." while it starts.
        script.Append("  if \"$TOOLS/altaserver\" status --data-root \"$ROOT\" 2>/dev/null | grep -q '^Health: ok'; then exit 0; fi\n  i=$((i+1)); sleep 1\ndone\n");
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

    // What ssh and the commands said, without the notice of a host key it just recorded, at most six lines.
    internal static string Lines(string text) => string.Join('\n', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(static line => !line.StartsWith("Warning: Permanently added", StringComparison.Ordinal)).Take(6));
}
