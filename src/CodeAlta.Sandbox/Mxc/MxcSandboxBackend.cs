using System.Runtime.InteropServices;
using Microsoft.Mxc.Sdk.V1;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>
/// The sandbox of Microsoft's mxc: a ProcessContainer on Windows, Seatbelt on macOS, Bubblewrap on Linux.
/// </summary>
/// <remarks>
/// A request runs only where the native container serves it: on Windows, the probe of every request must answer
/// the BaseContainer tier without changing the permissions of folders (mxc 1.0.1 falls back to that elsewhere).
/// Telemetry is never enabled.
/// </remarks>
public sealed class MxcSandboxBackend : ISandboxBackend
{
    private readonly Lazy<SandboxAvailability> _availability = new(Discover);

    /// <inheritdoc />
    public SandboxAvailability GetAvailability() => _availability.Value;

    /// <inheritdoc />
    public Task<ISandboxProcess> StartAsync(SandboxPolicy policy, string commandLine, string? proxyEndpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        if (GetAvailability().Refusal(policy.Network) is { } refusal) throw new SandboxUnavailableException(refusal);
        if (policy.Network == SandboxNetwork.Hosts && string.IsNullOrWhiteSpace(proxyEndpoint))
            throw new ArgumentException("The network of the policy goes through a proxy, and none is given.", nameof(proxyEndpoint));
        return StartCoreAsync(policy, commandLine, proxyEndpoint, cancellationToken);
    }

    private static async Task<ISandboxProcess> StartCoreAsync(SandboxPolicy policy, string commandLine, string? proxyEndpoint, CancellationToken cancellationToken)
    {
        MxcRequests.Prepare(policy);
        var request = MxcRequests.Create(policy, commandLine, proxyEndpoint);
        if (OperatingSystem.IsWindows())
        {
            ProbeOutput probe;
            try
            {
                probe = MxcContainer.Probe(request);
            }
            catch (Exception exception) when (exception is MxcException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new SandboxUnavailableException($"The Windows process container refused the sandbox: {exception.Message}", exception);
            }

            if (probe.Tier != IsolationTier.BaseContainer || probe.NeedsDaclAugmentation != false)
                throw new SandboxUnavailableException("The Windows process container cannot run this sandbox without changing the permissions of folders, which CodeAlta does not do. " +
                                                      "Windows 11 24H2 or 25H2 with the August 2026 update, or later, is needed.");
        }

        try
        {
            var process = await MxcContainer.SpawnAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new MxcSandboxProcess(process);
        }
        catch (MxcException exception)
        {
            throw new SandboxUnavailableException($"The sandbox did not start: {exception.Message}", exception);
        }
    }

    private static SandboxAvailability Discover()
    {
        var backend = OperatingSystem.IsWindows() ? "Windows process container" : OperatingSystem.IsMacOS() ? "macOS Seatbelt" : "Linux Bubblewrap";
        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture != Architecture.Arm64)
            return SandboxAvailability.Unavailable(backend, "the sandbox needs a Mac with Apple silicon.", null);
        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            if (!support.IsSupported) return SandboxAvailability.Unavailable(backend, support.Reason ?? "this system has no supported container.", SetupHint());
            var backends = MxcPlatform.GetAvailableBackends();
            if (OperatingSystem.IsWindows())
            {
                var container = backends.FirstOrDefault(static item => item.Backend == ContainmentBackend.ProcessContainer);
                if (container is not { Tier: IsolationTier.BaseContainer } || !container.Capabilities.Contains(BackendCapability.FilesystemDeniedPaths))
                    return SandboxAvailability.Unavailable(backend, "this version of Windows has no native process container.", SetupHint());
                var proxy = container.Capabilities.Contains(BackendCapability.IdentitylessLoopbackProxy);
                return new(true, backend, proxy ? null : "This version of Windows cannot filter the hosts of a sandbox.", proxy ? null : SetupHint(),
                    proxy ? new HashSet<SandboxNetwork> { SandboxNetwork.Off, SandboxNetwork.Hosts, SandboxNetwork.On } : new HashSet<SandboxNetwork> { SandboxNetwork.Off, SandboxNetwork.On });
            }

            if (OperatingSystem.IsLinux())
            {
                if (!backends.Any(static item => item.Backend == ContainmentBackend.Bubblewrap))
                    return SandboxAvailability.Unavailable(backend, "bubblewrap (`bwrap` 0.5 or later) is not installed or cannot create user namespaces.", SetupHint());
                // Any network but none goes through slirp4netns, which mxc looks for on PATH.
                var slirp = OnPath("slirp4netns");
                return new(true, backend, slirp ? null : "Without `slirp4netns`, a sandbox has no network at all.", slirp ? null : "Install the `slirp4netns` package for the network of the profiles.",
                    slirp ? new HashSet<SandboxNetwork> { SandboxNetwork.Off, SandboxNetwork.Hosts, SandboxNetwork.On } : new HashSet<SandboxNetwork> { SandboxNetwork.Off });
            }

            return backends.Any(static item => item.Backend == ContainmentBackend.Seatbelt)
                ? new(true, backend, null, null, new HashSet<SandboxNetwork> { SandboxNetwork.Off, SandboxNetwork.Hosts, SandboxNetwork.On })
                : SandboxAvailability.Unavailable(backend, "Seatbelt is not available (macOS 15 or later is needed).", null);
        }
        catch (Exception exception) when (exception is MxcException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return SandboxAvailability.Unavailable(backend, $"the container library did not load ({exception.Message}).", null);
        }
    }

    private static string? SetupHint()
        => OperatingSystem.IsWindows() ? "Install the Windows updates: Windows 11 24H2 or 25H2 with the August 2026 update (build 26100.9278 or 26200.9278) or later is needed."
            : OperatingSystem.IsLinux() ? "Install `bubblewrap` (0.5 or later) and `slirp4netns`, and allow unprivileged user namespaces (on Ubuntu 24.04, an AppArmor profile for `bwrap`)."
            : null;

    private static bool OnPath(string program)
        => (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, program)));
}
