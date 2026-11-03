using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Desktop.Fleet.MachineSetup;
using CodeAlta.Fleet.Identity;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Add a machine over SSH: the desktop installs CodeAlta Node on a computer it reaches with the system's OpenSSH client
/// and the person's keys, installs its service and pairs it as the device that manages it, step by step.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Gets what runs commands over SSH; the system's OpenSSH client unless a test gives another.</summary>
    internal ISshRunner? Ssh { get; init; }

    /// <summary>Gets the version of this desktop, the version of CodeAlta Node a setup installs by default.</summary>
    internal string DesktopVersion { get; init; } = DesktopCommandLine.Version;

    /// <summary>
    /// Sets up CodeAlta Node on a computer reached over SSH and pairs it with this computer, saying where each step stands:
    /// connect, check, install, service, pair; then <c>finished</c> with the machine, or <c>failed</c> with the step and why.
    /// </summary>
    [NeoRpcMethod("addOverSsh")]
    public NeoRpcChannel<MachineSetupEvent> AddOverSsh(MachineSshRequest request, CancellationToken cancellationToken)
        => new(AddOverSshAsync(request, cancellationToken), DesktopJsonContext.Default.MachineSetupEvent);

    internal async IAsyncEnumerable<MachineSetupEvent> AddOverSshAsync(MachineSshRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused)
        {
            yield return MachineSetupEvent.Failed(SshSetupSteps.Connect, refused, null);
            yield break;
        }

        if (!SshTarget.TryParse(request!.Target, out var target) || request.Port is < 1 or > 65535 || request.Name is { Length: > 64 }
            || request.DataRoot is { Length: > 1024 } || request.ToolPath is { Length: > 1024 } || request.PackageFolder is { Length: > 1024 }
            || request.Name is { } name && name.Length > 0 && name.Any(char.IsControl))
        {
            yield return MachineSetupEvent.Failed(SshSetupSteps.Connect, "invalid_target", null);
            yield break;
        }

        if (!ValidRemotePath(request.DataRoot) || !ValidRemotePath(request.ToolPath))
        {
            yield return MachineSetupEvent.Failed(SshSetupSteps.Check, "invalid_path", null);
            yield break;
        }

        var events = Channel.CreateUnbounded<MachineSetupEvent>(new UnboundedChannelOptions { SingleReader = true });
        var installer = new SshNodeInstaller(Ssh ?? new OpenSshRunner(), DesktopVersion);
        var setup = new SshNodeSetupRequest
        {
            Target = target, Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), Port = request.Port ?? CodeAlta.Fleet.Channels.FleetChannelProtocol.DefaultPort,
            DataRoot = Blank(request.DataRoot), ToolPath = Blank(request.ToolPath), PackageFolder = Blank(request.PackageFolder), AcceptNewHostKey = request.AcceptNewHostKey,
        };
        string? words = null;
        var run = Task.Run(async () =>
        {
            try
            {
                var machineId = await installer.RunAsync(setup, async (link, token) =>
                {
                    if (_fleet!.TryPreview(link, out var preview, out _)) words = preview.Words;
                    var machine = await _fleet!.PairAsync(link, token).ConfigureAwait(false);
                    return machine.DeviceId;
                }, step => events.Writer.TryWrite(new MachineSetupEvent(step.Step, step.State, step.Detail, step.Code, null, null, null)), cancellationToken).ConfigureAwait(false);
                var host = _fleet!.Find(machineId);
                events.Writer.TryWrite(new MachineSetupEvent(SshSetupSteps.Pair, "done", words, null, null, null, null));
                events.Writer.TryWrite(new MachineSetupEvent("finished", "done", null, null, machineId, host?.Machine.Name, words));
                Changed();
            }
            catch (SshSetupException exception)
            {
                events.Writer.TryWrite(MachineSetupEvent.Failed(exception.Step, exception.Code, exception.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or DeviceAuthenticationException or UnauthorizedAccessException)
            {
                events.Writer.TryWrite(MachineSetupEvent.Failed(SshSetupSteps.Pair, "pair_unreachable", exception.Message));
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        await run.ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A folder of the computer: absolute, or of the home (~/...), without what a shell would read otherwise.
    private static bool ValidRemotePath(string? path)
        => string.IsNullOrWhiteSpace(path) || (path.Trim() is var value && (value.StartsWith('/') || value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            && !value.Any(static c => char.IsControl(c)) && !value.Contains("..", StringComparison.Ordinal));
}

/// <summary>A computer to add over SSH, and how CodeAlta Node is set up there.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Target"><c>user@host</c>, <c>user@host:port</c>, or a host of the SSH configuration.</param>
/// <param name="Name">The name the machine gives itself; null for the name of the computer.</param>
/// <param name="Port">The port its node listens on; null for 2585.</param>
/// <param name="DataRoot">The profile of the node there; null for <c>~/.alta</c> (<c>~/.alta-node</c> where CodeAlta Desktop runs).</param>
/// <param name="ToolPath">The folder of the tool there; null for <c>~/.dotnet/tools</c>.</param>
/// <param name="PackageFolder">A folder of this computer with the packages to install; null for nuget.org.</param>
/// <param name="AcceptNewHostKey">Whether a host key ssh does not know yet is accepted.</param>
internal sealed record MachineSshRequest(string ExpectedEpoch, string Target, string? Name, int? Port, string? DataRoot, string? ToolPath, string? PackageFolder,
    bool AcceptNewHostKey);

/// <summary>Where a setup over SSH stands.</summary>
/// <param name="Step"><c>connect</c>, <c>check</c>, <c>install</c>, <c>service</c>, <c>pair</c>, or <c>finished</c>.</param>
/// <param name="State"><c>running</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Detail">What the step checked or changed, or what the failure said.</param>
/// <param name="Code">Why it failed (<c>host_key_unknown</c>, <c>auth_failed</c>, <c>unknown_host</c>, <c>unreachable</c>, <c>unsupported_system</c>, <c>dotnet_missing</c>...).</param>
/// <param name="MachineId">The machine, once paired.</param>
/// <param name="MachineName">Its name.</param>
/// <param name="Words">The six words of its key.</param>
internal sealed record MachineSetupEvent(string Step, string State, string? Detail, string? Code, string? MachineId, string? MachineName, string? Words)
{
    internal static MachineSetupEvent Failed(string step, string code, string? detail) => new(step, "failed", detail, code, null, null, null);
}
