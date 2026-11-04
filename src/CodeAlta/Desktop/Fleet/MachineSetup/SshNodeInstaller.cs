using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Desktop.Fleet.MachineSetup;

/// <summary>What the person asks: a computer reached over SSH, and how CodeAlta Node is set up there.</summary>
internal sealed record SshNodeSetupRequest
{
    /// <summary>Gets the computer.</summary>
    public required SshTarget Target { get; init; }

    /// <summary>Gets the name its node gives itself; null for the name of the computer.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the port its node listens on (2585 by default).</summary>
    public int Port { get; init; } = CodeAlta.Fleet.Channels.FleetChannelProtocol.DefaultPort;

    /// <summary>Gets the profile of the node there (an absolute path or <c>~/...</c>); null for <c>~/.alta</c>, or <c>~/.alta-node</c> when CodeAlta Desktop runs there.</summary>
    public string? DataRoot { get; init; }

    /// <summary>Gets the folder the tool is installed in there; null for <c>~/.dotnet/tools</c>.</summary>
    public string? ToolPath { get; init; }

    /// <summary>Gets a folder of this computer with the packages of CodeAlta Node to install from; null for nuget.org.</summary>
    public string? PackageFolder { get; init; }

    /// <summary>Gets the version to install; null for the one of the packages of <see cref="PackageFolder"/>, or the version of this desktop.</summary>
    public string? Version { get; init; }

    /// <summary>Gets whether a host key ssh does not know yet is accepted (the person said they trust the computer).</summary>
    public bool AcceptNewHostKey { get; init; }

    /// <summary>Gets whether the runtime package is installed even where a .NET SDK is (the path of machines without one).</summary>
    internal bool PreferRuntimePackage { get; init; }
}

/// <summary>The steps of a setup, as the page lists them.</summary>
internal static class SshSetupSteps
{
    public const string Connect = "connect";
    public const string Check = "check";
    public const string Install = "install";
    public const string Service = "service";
    public const string Pair = "pair";
}

/// <summary>Where one step of a setup stands.</summary>
/// <param name="Step">The step (<see cref="SshSetupSteps"/>).</param>
/// <param name="State"><c>running</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Detail">What it checked or changed, in words; null while it runs without news.</param>
/// <param name="Code">Why it failed, for the page to say what to do (<c>host_key_unknown</c>, <c>auth_failed</c>...); null otherwise.</param>
internal sealed record SshSetupProgress(string Step, string State, string? Detail = null, string? Code = null);

/// <summary>What the probe of a computer found.</summary>
internal sealed record SshMachineFacts(string Os, string Architecture, string? RuntimeIdentifier, string Home, string User, string? Dotnet, string? Sdk, string? Runtime,
    bool DesktopRuns, string? Linger, string? Hostname)
{
    /// <summary>Gets the operating system in words.</summary>
    public string OsName => Os switch { "Linux" => "Linux", "Darwin" => "macOS", _ => Os };

    /// <summary>Gets the firewall that runs there and can block a port (<c>ufw</c>); null when none is known.</summary>
    public string? Firewall { get; init; }

    /// <summary>Gets whether Tailscale is installed there.</summary>
    public bool Tailscale { get; init; }
}

/// <summary>A setup that stopped, with what the person should know and do.</summary>
internal sealed class SshSetupException(string step, string code, string message) : Exception(message)
{
    public string Step { get; } = step;

    public string Code { get; } = code;
}

/// <summary>
/// Installs CodeAlta Node on a computer reached over SSH, as its owner would in a terminal there, and pairs it with this
/// desktop as the device that manages it: connect and probe (system, architecture, .NET), install the tool
/// (<c>dotnet tool update</c>, or the runtime package where only the .NET runtime is), install its service
/// (<c>altanode install --listen-all</c>), then <c>altanode pair --manage</c>, whose link this desktop redeems at once over
/// the channel it pins to the node's key. Each step can run again: a retry starts over and changes only what is missing.
/// </summary>
internal sealed partial class SshNodeInstaller(ISshRunner ssh, string desktopVersion)
{
    /// <summary>The package of the tool.</summary>
    internal const string PackageId = "CodeAlta.Node";

    private static readonly TimeSpan PairTimeout = TimeSpan.FromMinutes(3);

    // How long the first connection and its probe may take: a computer that does not answer by then is not reached.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Runs the setup; <paramref name="pair"/> redeems the link the node prints and returns the words of its key.
    /// </summary>
    /// <param name="request">The computer and the options.</param>
    /// <param name="pair">Pairs this desktop with the link the node made; returns the device id of the machine.</param>
    /// <param name="progress">Told where each step stands.</param>
    /// <param name="cancellationToken">Stops the setup.</param>
    /// <returns>The device id of the machine, now paired and managed from here.</returns>
    /// <exception cref="SshSetupException">A step failed; its code says why.</exception>
    public async Task<string> RunAsync(SshNodeSetupRequest request, Func<string, CancellationToken, Task<string>> pair, Action<SshSetupProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(progress);
        var target = request.Target;

        progress(new(SshSetupSteps.Connect, "running", target.Destination));
        SshResult probe;
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(ConnectTimeout);
            try
            {
                probe = await ssh.RunAsync(target, ProbeScript, request.AcceptNewHostKey, null, connecting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Fail(SshSetupSteps.Connect, "unreachable", $"No answer within {(int)ConnectTimeout.TotalSeconds} s.");
            }
        }

        if (probe.ExitCode == SshResult.ConnectionFailed) throw await ConnectionFailureAsync(ssh, target, probe.Error, cancellationToken).ConfigureAwait(false);
        var facts = ReadFacts(probe.Output);
        progress(new(SshSetupSteps.Connect, "done", $"{facts.User}@{facts.Hostname ?? target.Host}"));

        progress(new(SshSetupSteps.Check, "running"));
        if (facts.RuntimeIdentifier is not { } rid)
            throw Fail(SshSetupSteps.Check, "unsupported_system", $"{facts.OsName} {facts.Architecture}");
        var useSdk = facts.Sdk is not null && !request.PreferRuntimePackage;
        if (facts.Dotnet is null || facts.Runtime is null && facts.Sdk is null)
            throw Fail(SshSetupSteps.Check, "dotnet_missing", $"{facts.OsName} {facts.Architecture}");
        if (!useSdk && facts.Runtime is null) throw Fail(SshSetupSteps.Check, "dotnet_missing", $"{facts.OsName} {facts.Architecture}");
        var dataRoot = request.DataRoot is { Length: > 0 } root ? root : facts.DesktopRuns ? "~/.alta-node" : null;
        var toolPath = request.ToolPath is { Length: > 0 } tools ? tools : "~/.dotnet/tools";
        var dotnetText = useSdk ? $".NET SDK {facts.Sdk}" : $".NET {facts.Runtime}";
        progress(new(SshSetupSteps.Check, "done", $"{facts.OsName} {facts.Architecture} · {dotnetText}"
            + (dataRoot is not null && request.DataRoot is null ? $" · CodeAlta Desktop runs there: the node gets its own profile, {dataRoot}" : "")));

        progress(new(SshSetupSteps.Install, "running"));
        var packages = request.PackageFolder is { Length: > 0 } folder ? LocalPackages(folder, rid) : null;
        var version = request.Version is { Length: > 0 } wanted ? wanted : packages?.Version ?? ReleaseVersion(desktopVersion);
        string? remoteSource = null;
        if (packages is not null)
        {
            var stage = await ssh.RunAsync(target, "set -e\nd=\"$HOME/.cache/codealta-setup\"\nrm -rf \"$d\"\nmkdir -p \"$d\"\nprintf '%s\\n' \"$d\"\n",
                request.AcceptNewHostKey, null, cancellationToken).ConfigureAwait(false);
            remoteSource = stage.Output.Trim();
            if (!stage.Succeeded || remoteSource.Length == 0) throw Fail(SshSetupSteps.Install, "copy_failed", FirstLines(stage.Error));
            var copied = await ssh.CopyAsync(target, packages.Files, remoteSource, request.AcceptNewHostKey, cancellationToken).ConfigureAwait(false);
            if (!copied.Succeeded) throw Fail(SshSetupSteps.Install, "copy_failed", FirstLines(copied.Error));
        }

        var install = await ssh.RunAsync(target, useSdk ? ToolInstallScript(facts, toolPath, version, remoteSource) : RuntimeInstallScript(facts, rid, toolPath, version, remoteSource),
            request.AcceptNewHostKey, null, cancellationToken).ConfigureAwait(false);
        if (!install.Succeeded) throw Fail(SshSetupSteps.Install, useSdk ? "tool_install_failed" : "runtime_install_failed", FirstLines(install.Error + install.Output, 6));
        progress(new(SshSetupSteps.Install, "done", $"CodeAlta Node {version} · {toolPath}"));

        progress(new(SshSetupSteps.Service, "running"));
        var service = await ssh.RunAsync(target, ServiceScript(facts, toolPath, dataRoot, request.Port, request.Name), request.AcceptNewHostKey, null, cancellationToken)
            .ConfigureAwait(false);
        if (!service.Succeeded) throw Fail(SshSetupSteps.Service, "service_failed", FirstLines(service.Error + service.Output, 6));
        progress(new(SshSetupSteps.Service, "done", ServiceDetail(facts, request.Port)));

        progress(new(SshSetupSteps.Pair, "running"));
        using var pairing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pairing.CancelAfter(PairTimeout);
        var link = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = ssh.RunAsync(target, PairScript(facts, toolPath, dataRoot), request.AcceptNewHostKey, line =>
        {
            if (PairLinkPattern().Match(line) is { Success: true } match) link.TrySetResult(match.Value);
        }, pairing.Token);
        var first = await Task.WhenAny(link.Task, waiting).ConfigureAwait(false);
        if (first != link.Task)
        {
            var ended = await waiting.ConfigureAwait(false);
            throw Fail(SshSetupSteps.Pair, "pair_failed", FirstLines(ended.Error + ended.Output, 6));
        }

        string machineId;
        try
        {
            machineId = await pair(await link.Task.ConfigureAwait(false), pairing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await pairing.CancelAsync().ConfigureAwait(false);
            throw Fail(SshSetupSteps.Pair, LocalNetworkAccess.IsBlockedHere(exception, OperatingSystem.IsMacOS()) ? "local_network_blocked" : "pair_unreachable", exception.Message);
        }

        // altanode pair ends once the link is redeemed.
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { await pairing.CancelAsync().ConfigureAwait(false); }
        return machineId;
    }

    /// <summary>
    /// The failure of a first connection: its code, and what ssh said; for a host key ssh does not know yet, the fingerprint
    /// of that key instead, which the person compares before trusting it.
    /// </summary>
    internal static async Task<SshSetupException> ConnectionFailureAsync(ISshRunner ssh, SshTarget target, string error, CancellationToken cancellationToken)
    {
        var code = ConnectionCode(error);
        var detail = code == "host_key_unknown" ? await ssh.ReadHostKeyAsync(target, cancellationToken).ConfigureAwait(false) : null;
        return new SshSetupException(SshSetupSteps.Connect, code, detail ?? FirstLines(error));
    }

    /// <summary>The words of a failure: what ssh said when it could not connect.</summary>
    internal static string ConnectionCode(string error)
    {
        if (error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No ED25519 host key is known", StringComparison.OrdinalIgnoreCase)
            || error.Contains("host key is known", StringComparison.OrdinalIgnoreCase)) return "host_key_unknown";
        if (error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase)) return "host_key_changed";
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "auth_failed";
        if (error.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase) || error.Contains("No such host", StringComparison.OrdinalIgnoreCase)) return "unknown_host";
        if (error.Contains("could not start", StringComparison.OrdinalIgnoreCase)) return "ssh_missing";
        return "unreachable";
    }

    // The system, the architecture and .NET of the computer; POSIX sh, for Linux and macOS. dotnet is looked for where
    // installers put it, and on the PATH of a login shell, which a command run over SSH does not have on macOS.
    // Its ends of line are those of sh whatever the checkout of this file.
    internal static readonly string ProbeScript = """
        echo "os=$(uname -s 2>/dev/null)"
        echo "arch=$(uname -m 2>/dev/null)"
        echo "home=$HOME"
        echo "user=$(id -un 2>/dev/null)"
        echo "hostname=$(hostname 2>/dev/null)"
        DOTNET=""
        for candidate in "$(command -v dotnet 2>/dev/null)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /opt/homebrew/bin/dotnet /usr/local/bin/dotnet; do
          if [ -n "$candidate" ] && [ -x "$candidate" ]; then DOTNET="$candidate"; break; fi
        done
        if [ -z "$DOTNET" ] && [ -n "${SHELL:-}" ]; then DOTNET=$("$SHELL" -lc 'command -v dotnet' 2>/dev/null | tail -n 1); fi
        echo "dotnet=$DOTNET"
        if [ -n "$DOTNET" ]; then
          "$DOTNET" --list-sdks 2>/dev/null | sed 's/^/sdk=/'
          "$DOTNET" --list-runtimes 2>/dev/null | grep 'Microsoft.NETCore.App' | sed 's/^/runtime=/'
        fi
        if command -v loginctl >/dev/null 2>&1; then echo "linger=$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null)"; fi
        if pgrep -x alta >/dev/null 2>&1; then echo "desktop=running"; fi
        if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet ufw 2>/dev/null; then echo "firewall=ufw"; fi
        if command -v tailscale >/dev/null 2>&1; then echo "tailscale=yes"; fi
        if [ -f /etc/alpine-release ]; then echo "libc=musl"; fi
        exit 0
        """.ReplaceLineEndings("\n");

    /// <summary>Reads what the probe printed.</summary>
    internal static SshMachineFacts ReadFacts(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sdk = null, runtime = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var equal = line.IndexOf('=');
            if (equal <= 0) continue;
            var (key, value) = (line[..equal], line[(equal + 1)..]);
            // The newest SDK and runtime of .NET 10 or later: "10.0.114 [/usr/lib/dotnet/sdk]", "Microsoft.NETCore.App 10.0.14 [...]".
            if (key == "sdk") sdk = Newest(sdk, value.Split(' ')[0]);
            else if (key == "runtime" && value.Split(' ') is [_, var version, ..]) runtime = Newest(runtime, version);
            else values[key] = value;
        }

        var os = values.GetValueOrDefault("os") ?? "";
        var arch = values.GetValueOrDefault("arch") ?? "";
        var rid = (os, arch) switch
        {
            ("Linux", "x86_64" or "amd64") => values.GetValueOrDefault("libc") == "musl" ? "linux-musl-x64" : "linux-x64",
            ("Linux", "aarch64" or "arm64") => values.GetValueOrDefault("libc") == "musl" ? "linux-musl-arm64" : "linux-arm64",
            ("Darwin", "arm64") => "osx-arm64",
            ("Darwin", "x86_64") => "osx-x64",
            _ => null,
        };
        return new SshMachineFacts(os, arch, rid, values.GetValueOrDefault("home") ?? "~", values.GetValueOrDefault("user") ?? "", NullIfEmpty(values.GetValueOrDefault("dotnet")),
            AtLeast10(sdk), AtLeast10(runtime), values.GetValueOrDefault("desktop") == "running", NullIfEmpty(values.GetValueOrDefault("linger")),
            NullIfEmpty(values.GetValueOrDefault("hostname")))
        {
            Firewall = NullIfEmpty(values.GetValueOrDefault("firewall")), Tailscale = values.GetValueOrDefault("tailscale") == "yes",
        };

        static string? Newest(string? current, string candidate)
            => current is null || Parse(candidate) > Parse(current) ? candidate : current;
        static Version Parse(string text) => System.Version.TryParse(text.Split('-')[0], out var value) ? value : new Version(0, 0);
        static string? AtLeast10(string? version) => version is not null && Parse(version).Major >= 10 ? version : null;
        static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The packages of a folder of this computer: the tool package and, when there, the runtime package of the computer's system.</summary>
    internal static LocalPackageSet LocalPackages(string folder, string rid) => LocalPackages(folder, rid, PackageId);

    /// <summary>The packages of a folder of this computer for a package id: its tool package and the runtime package of the computer's system.</summary>
    internal static LocalPackageSet LocalPackages(string folder, string rid, string packageId)
    {
        if (!Directory.Exists(folder)) throw Fail(SshSetupSteps.Install, "packages_missing", folder);
        // CodeAlta.Node.<version>.nupkg, and CodeAlta.Node.<rid>.<version>.nupkg: the newest version that has both.
        var tools = Directory.GetFiles(folder, packageId + ".*.nupkg").Select(path => (Path: path, Version: VersionOf(Path.GetFileName(path), packageId)))
            .Where(static file => file.Version is not null).ToArray();
        var runtimes = Directory.GetFiles(folder, $"{packageId}.{rid}.*.nupkg").Select(path => (Path: path, Version: VersionOf(Path.GetFileName(path), $"{packageId}.{rid}")))
            .Where(static file => file.Version is not null).ToArray();
        var best = tools.Where(tool => runtimes.Any(runtime => runtime.Version == tool.Version))
            .OrderByDescending(static tool => tool.Version, Comparer<string?>.Create(static (left, right) => CompareVersions(left!, right!))).FirstOrDefault();
        if (best.Path is null) throw Fail(SshSetupSteps.Install, "packages_missing", $"{folder} ({packageId} and {packageId}.{rid})");
        return new LocalPackageSet(best.Version!, [best.Path, runtimes.First(runtime => runtime.Version == best.Version).Path]);
    }

    private static string? VersionOf(string fileName, string id)
    {
        var prefix = id + ".";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) return null;
        var version = fileName[prefix.Length..^".nupkg".Length];
        return version.Length > 0 && char.IsDigit(version[0]) ? version : null;
    }

    private static int CompareVersions(string left, string right)
        => NuGetVersionKey(left).CompareTo(NuGetVersionKey(right)) is var core and not 0 ? core : string.CompareOrdinal(left, right);

    private static Version NuGetVersionKey(string version) => System.Version.TryParse(version.Split('-', '+')[0], out var value) ? value : new Version(0, 0);

    /// <summary>The version of this desktop as a package version: its build metadata dropped.</summary>
    internal static string ReleaseVersion(string version) => version.Split('+')[0];

    /// <summary>Quotes a word for sh, or a path of the home (<c>~/x</c>) as <c>"$HOME"/'x'</c>.</summary>
    internal static string Quote(string value)
    {
        if (value == "~") return "\"$HOME\"";
        if (value.StartsWith("~/", StringComparison.Ordinal)) return "\"$HOME\"/" + Quote(value[2..]);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    internal static string DotnetPrelude(SshMachineFacts facts)
    {
        var dotnet = Quote(facts.Dotnet!);
        // The tool's launcher finds the runtime with DOTNET_ROOT, the folder dotnet really is in (a link of /usr/bin on Ubuntu).
        // The PATH of a login shell of the person, which the service keeps: its sessions find git, gh and node there.
        const string loginPath = "LOGIN_PATH=$(\"${SHELL:-/bin/sh}\" -lc 'printf \"\\n%s\" \"$PATH\"' 2>/dev/null </dev/null | tail -n 1 || true)\n"
            + "if [ -n \"$LOGIN_PATH\" ]; then export PATH=\"$LOGIN_PATH\"; fi\n";
        return $"set -e\n{loginPath}DOTNET={dotnet}\nREAL=$(readlink -f \"$DOTNET\" 2>/dev/null || echo \"$DOTNET\")\nexport DOTNET_ROOT=$(dirname \"$REAL\")\nexport DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1\n";
    }

    private static string ToolInstallScript(SshMachineFacts facts, string toolPath, string version, string? source)
    {
        var script = new StringBuilder(DotnetPrelude(facts));
        script.Append("TOOLS=").Append(Quote(toolPath)).Append('\n');
        script.Append("mkdir -p \"$TOOLS\"\n");
        // update installs the tool when it is missing, and moves it to the version asked otherwise.
        script.Append("\"$DOTNET\" tool update --tool-path \"$TOOLS\" ").Append(PackageId).Append(" --version ").Append(Quote(version)).Append(" --allow-downgrade");
        if (source is not null) script.Append(" --add-source ").Append(Quote(source));
        script.Append(" 1>&2\n\"$TOOLS/altanode\" --version\n");
        return script.ToString();
    }

    // The runtime package (CodeAlta.Node.<rid>): its application unpacked beside the others, and a link to it where the tool would be.
    private static string RuntimeInstallScript(SshMachineFacts facts, string rid, string toolPath, string version, string? source)
    {
        var id = $"{PackageId}.{rid}";
        var lower = id.ToLowerInvariant();
        var script = new StringBuilder(DotnetPrelude(facts));
        script.Append("TOOLS=").Append(Quote(toolPath)).Append('\n');
        script.Append("APP=\"$HOME/.local/share/codealta-node/").Append(version).Append("\"\n");
        script.Append("PKG=\"$HOME/.cache/codealta-setup/").Append(id).Append('.').Append(version).Append(".nupkg\"\n");
        script.Append("mkdir -p \"$TOOLS\" \"$APP\" \"$(dirname \"$PKG\")\"\n");
        if (source is null)
            script.Append("curl -fsSL -o \"$PKG\" ").Append(Quote($"https://api.nuget.org/v3-flatcontainer/{lower}/{version.ToLowerInvariant()}/{lower}.{version.ToLowerInvariant()}.nupkg")).Append('\n');
        else
            script.Append("PKG=").Append(Quote($"{source}/{id}.{version}.nupkg")).Append('\n');
        script.Append("rm -rf \"$APP\" && mkdir -p \"$APP\"\n");
        script.Append("if command -v unzip >/dev/null 2>&1; then unzip -q -o \"$PKG\" 'tools/*' -d \"$APP\";\n");
        script.Append("elif command -v bsdtar >/dev/null 2>&1; then bsdtar -xf \"$PKG\" -C \"$APP\" tools;\n");
        script.Append("else python3 -c 'import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' \"$PKG\" \"$APP\"; fi\n");
        script.Append("EXE=$(find \"$APP/tools\" -type f -name altanode | head -n 1)\n");
        script.Append("[ -n \"$EXE\" ] || { echo 'The package holds no altanode.' >&2; exit 3; }\n");
        script.Append("chmod +x \"$EXE\"\n");
        // A launcher that names the runtime, so that the service starts it whatever its environment.
        script.Append("printf '#!/bin/sh\\nexport DOTNET_ROOT=%s\\nexec %s \"$@\"\\n' \"$DOTNET_ROOT\" \"$EXE\" > \"$TOOLS/altanode\"\n");
        script.Append("chmod +x \"$TOOLS/altanode\"\n\"$TOOLS/altanode\" --version\n");
        return script.ToString();
    }

    private static string ServiceScript(SshMachineFacts facts, string toolPath, string? dataRoot, int port, string? name)
    {
        var script = new StringBuilder(DotnetPrelude(facts));
        script.Append("TOOLS=").Append(Quote(toolPath)).Append('\n');
        if (dataRoot is not null) script.Append("ROOT=").Append(Quote(dataRoot)).Append("\nmkdir -p \"$ROOT\"\nROOT=$(cd \"$ROOT\" && pwd)\n");
        script.Append("\"$TOOLS/altanode\" install --listen-all --port ").Append(port.ToString(CultureInfo.InvariantCulture));
        if (dataRoot is not null) script.Append(" --data-root \"$ROOT\"");
        if (name is { Length: > 0 }) script.Append(" --name ").Append(Quote(name));
        script.Append('\n');
        return script.ToString();
    }

    // Waits for the node the service started to listen, then makes a link for a device that manages it, and waits for it.
    private static string PairScript(SshMachineFacts facts, string toolPath, string? dataRoot)
    {
        var script = new StringBuilder(DotnetPrelude(facts));
        script.Append("TOOLS=").Append(Quote(toolPath)).Append('\n');
        var root = "";
        if (dataRoot is not null)
        {
            script.Append("ROOT=").Append(Quote(dataRoot)).Append("\nROOT=$(cd \"$ROOT\" && pwd)\n");
            root = " --data-root \"$ROOT\"";
        }

        script.Append("i=0\nwhile [ $i -lt 60 ]; do\n");
        script.Append("  if \"$TOOLS/altanode\" status").Append(root).Append(" 2>/dev/null | grep -q '^Listening on'; then break; fi\n");
        script.Append("  i=$((i+1)); sleep 1\ndone\n");
        script.Append("exec \"$TOOLS/altanode\" pair --manage --minutes 5").Append(root).Append('\n');
        return script.ToString();
    }

    private static string ServiceDetail(SshMachineFacts facts, int port) => facts.Os switch
    {
        "Linux" => $"systemd --user unit, port {port}" + (facts.Linger == "yes" ? "" : " · lingering on, so that it runs without a login"),
        "Darwin" => $"LaunchAgent, port {port}",
        _ => $"port {port}",
    };

    private static SshSetupException Fail(string step, string code, string message) => new(step, code, message);

    private static string FirstLines(string text, int count = 3)
        => string.Join('\n', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(static line => !line.StartsWith("Warning: Permanently added", StringComparison.Ordinal)).Take(count));

    [GeneratedRegex(@"codealta-pair://\S+")]
    private static partial Regex PairLinkPattern();
}

/// <summary>The packages a setup copies from this computer.</summary>
/// <param name="Version">Their version.</param>
/// <param name="Files">The tool package and the runtime package.</param>
internal sealed record LocalPackageSet(string Version, IReadOnlyList<string> Files);
