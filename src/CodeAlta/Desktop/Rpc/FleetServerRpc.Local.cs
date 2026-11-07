using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Desktop.Fleet.MachineSetup;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>Create my server on this computer: in one go, without copying a setup code from one window to another.</summary>
internal sealed partial class FleetServerService
{
    /// <summary>Gets what runs the commands of the setup on this computer; the system's processes unless a test gives others.</summary>
    internal ILocalProcesses? Processes { get; init; }

    /// <summary>Gets the version of this desktop, the version of CodeAlta Server installed when none is.</summary>
    internal string DesktopVersion { get; init; } = DesktopCommandLine.Version;

    /// <summary>
    /// Sets up CodeAlta Server on this computer and makes this computer its owner, saying where each step stands: find,
    /// install, start, claim; then <c>finished</c> with the link of the server, or <c>failed</c> with the step and why.
    /// </summary>
    [NeoRpcMethod("createServer")]
    public NeoRpcChannel<MachineSetupEvent> CreateServer(FleetServerSetupRequest request, CancellationToken cancellationToken)
        => new(string.IsNullOrWhiteSpace(request?.Target)
            ? CreateOnThisComputerAsync(request is null ? null! : new FleetServerLocalRequest(request.ExpectedEpoch, request.OwnerName, request.OrganizationName, request.Port,
                request.DataRoot, request.ToolPath, request.PackageFolder), cancellationToken)
            : CreateOverSshAsync(request!, cancellationToken), DesktopJsonContext.Default.MachineSetupEvent);

    internal async IAsyncEnumerable<MachineSetupEvent> CreateOnThisComputerAsync(FleetServerLocalRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused)
        {
            yield return MachineSetupEvent.Failed(LocalServerSteps.Find, refused, null);
            yield break;
        }

        if (request!.Port is < 1 or > 65535 || request.OwnerName is not { Length: > 0 and <= 128 } owner || request.OrganizationName is { Length: > 128 }
            || !LocalFolder(request.DataRoot) || !LocalFolder(request.ToolPath) || !LocalFolder(request.PackageFolder))
        {
            yield return MachineSetupEvent.Failed(LocalServerSteps.Find, "invalid_request", null);
            yield break;
        }

        var events = Channel.CreateUnbounded<MachineSetupEvent>(new UnboundedChannelOptions { SingleReader = true });
        var setup = new LocalServerSetup(Processes ?? new SystemProcesses(), DesktopVersion);
        var run = Task.Run(async () =>
        {
            try
            {
                var linkId = await setup.RunAsync(new LocalServerRequest
                {
                    Port = request.Port ?? 2586, DataRoot = Blank(request.DataRoot), ToolPath = Blank(request.ToolPath), PackageFolder = Blank(request.PackageFolder),
                    OwnerName = owner.Trim(), OrganizationName = Blank(request.OrganizationName),
                }, async (address, code, token) =>
                {
                    try
                    {
                        return (await _servers!.CreateServerAsync(address, code, owner.Trim(), Blank(request.OrganizationName), token).ConfigureAwait(false)).Id;
                    }
                    catch (FleetServerException exception)
                    {
                        throw new SshSetupException(LocalServerSteps.Claim, "claim_refused", exception.Message);
                    }
                    catch (HttpRequestException exception)
                    {
                        throw new SshSetupException(LocalServerSteps.Claim, "claim_refused", exception.Message);
                    }
                }, step => events.Writer.TryWrite(new MachineSetupEvent(step.Step, step.State, step.Detail, step.Code, null, null, null)), cancellationToken).ConfigureAwait(false);
                events.Writer.TryWrite(Finished(linkId, await TryCreateRecoveryKeyAsync(linkId, cancellationToken).ConfigureAwait(false)));
            }
            catch (SshSetupException exception)
            {
                events.Writer.TryWrite(MachineSetupEvent.Failed(exception.Step, exception.Code, exception.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                events.Writer.TryWrite(MachineSetupEvent.Failed(LocalServerSteps.Start, "server_start_failed", exception.Message));
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        await run.ConfigureAwait(false);
    }

    /// <summary>Gets what runs commands over SSH; the system's OpenSSH client unless a test gives another.</summary>
    internal ISshRunner? Ssh { get; init; }

    /// <summary>Gets whether a server answers at an address from this computer; its health request unless a test says otherwise.</summary>
    internal Func<Uri, string?, CancellationToken, Task<ReachProblem?>>? Reachable { get; init; }

    /// <summary>
    /// Sets up CodeAlta Server on a computer reached over SSH and makes this computer its owner, saying where each step
    /// stands: connect, check, install, service, reach, claim; then <c>finished</c> with the link, or <c>failed</c> with why.
    /// </summary>
    internal async IAsyncEnumerable<MachineSetupEvent> CreateOverSshAsync(FleetServerSetupRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused)
        {
            yield return MachineSetupEvent.Failed(SshSetupSteps.Connect, refused, null);
            yield break;
        }

        Uri? address = null;
        if (!SshTarget.TryParse(request!.Target, out var target) || request.Port is < 1 or > 65535 || request.OwnerName is not { Length: > 0 and <= 128 } owner
            || request.OrganizationName is { Length: > 128 } || !LocalFolder(request.PackageFolder) || !RemoteFolder(request.DataRoot) || !RemoteFolder(request.ToolPath)
            || request.Address is { Length: > 0 } text && !CodeAlta.Fleet.Server.FleetServerAddress.TryParse(text.Trim(), out address))
        {
            yield return MachineSetupEvent.Failed(SshSetupSteps.Connect, "invalid_request", null);
            yield break;
        }

        var events = Channel.CreateUnbounded<MachineSetupEvent>(new UnboundedChannelOptions { SingleReader = true });
        var installer = new SshServerInstaller(Ssh ?? new OpenSshRunner(), DesktopVersion);
        var run = Task.Run(async () =>
        {
            try
            {
                var linkId = await installer.RunAsync(new SshServerSetupRequest
                {
                    Target = target, Port = request.Port ?? 2586, Address = address, DataRoot = Blank(request.DataRoot), ToolPath = Blank(request.ToolPath),
                    PackageFolder = Blank(request.PackageFolder), TrustedHostKey = TrustedHostKey(request.TrustedHostKey), OwnCertificate = request.OwnCertificate,
                    // Given to sudo -S on the machine for this setup only: never kept, never logged.
                    AlwaysOn = request.AlwaysOn, SudoPassword = request.AlwaysOn && !string.IsNullOrEmpty(request.SudoPassword) ? request.SudoPassword : null,
                }, Reachable ?? AnswersAsync, async (linkAddress, setupAddress, code, pin, token) =>
                {
                    try
                    {
                        return (await _servers!.CreateServerAsync(linkAddress, setupAddress, code, owner.Trim(), Blank(request.OrganizationName), pin, token).ConfigureAwait(false)).Id;
                    }
                    catch (Exception exception) when (exception is FleetServerException or HttpRequestException or InvalidOperationException)
                    {
                        throw new SshSetupException(SshServerSteps.Claim, "claim_refused", exception.Message);
                    }
                }, step => events.Writer.TryWrite(new MachineSetupEvent(step.Step, step.State, step.Detail, step.Code, null, null, null)), cancellationToken).ConfigureAwait(false);
                events.Writer.TryWrite(Finished(linkId, await TryCreateRecoveryKeyAsync(linkId, cancellationToken).ConfigureAwait(false)));
            }
            catch (SshSetupException exception)
            {
                events.Writer.TryWrite(MachineSetupEvent.Failed(exception.Step, exception.Code, exception.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                events.Writer.TryWrite(MachineSetupEvent.Failed(SshSetupSteps.Connect, "unreachable", exception.Message));
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        await run.ConfigureAwait(false);
    }

    // The server is claimed: its link, and the words of the owner's recovery key made with it, which the page shows once.
    private static MachineSetupEvent Finished(string linkId, string? recoveryWords) => new("finished", "done", null, null, linkId, null, null, recoveryWords);

    // The health request of the server, from this computer, as the machines will reach it: null when it answers, otherwise
    // what the last attempt met.
    private static async Task<ReachProblem?> AnswersAsync(Uri address, string? certificatePin, CancellationToken cancellationToken)
    {
        // A server with its own certificate answers through the pin only: the check is the one every device makes.
        using var http = certificatePin is null ? new HttpClient() : new HttpClient(CodeAlta.Fleet.Protocol.FleetServerCertificates.CreateHandler(certificatePin), disposeHandler: true);
        http.Timeout = TimeSpan.FromSeconds(10);
        ReachProblem? problem = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri(address, "api/v1/server/health"), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return null;
                problem = new ReachProblem($"HTTP {(int)response.StatusCode}");
            }
            catch (HttpRequestException exception)
            {
                problem = LocalNetworkAccess.ProblemOf(exception, OperatingSystem.IsMacOS());
                // macOS refuses at once and again: no need to wait for the other attempts.
                if (problem.BlockedHere) return problem;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                problem = new ReachProblem("No answer within 10 s");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return problem ?? new ReachProblem("No answer");
    }

    private static bool RemoteFolder(string? path)
        => string.IsNullOrWhiteSpace(path) || path.Trim() is var value && value.Length <= 1024 && (value.StartsWith('/') || value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            && !value.Any(char.IsControl) && !value.Contains("..", StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The fingerprint of a host key the person trusted, as it was shown (SHA256:… with its type): anything else trusts nothing.
    internal static string? TrustedHostKey(string? value)
        => value?.Trim() is { Length: > 7 and <= 128 } text && text.StartsWith("SHA256:", StringComparison.Ordinal) && !text.Any(char.IsControl) ? text : null;

    private static bool LocalFolder(string? path) => string.IsNullOrWhiteSpace(path) || path.Length <= 1024 && Path.IsPathFullyQualified(path.Trim());
}

/// <summary>CodeAlta Server to set up on this computer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="OwnerName">The name of the owner.</param>
/// <param name="OrganizationName">The name of the organization; null for the server's default.</param>
/// <param name="Port">Its port on the loopback; null for 2586.</param>
/// <param name="DataRoot">Its data folder; null for <c>~/.altaserver</c>.</param>
/// <param name="ToolPath">The folder of the tool; null for the global tools.</param>
/// <param name="PackageFolder">A folder with its packages; null for nuget.org.</param>
internal sealed record FleetServerLocalRequest(string ExpectedEpoch, string OwnerName, string? OrganizationName, int? Port, string? DataRoot, string? ToolPath,
    string? PackageFolder);

/// <summary>CodeAlta Server to set up on a computer reached over SSH, or on this computer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Target"><c>user@host</c>, <c>user@host:port</c>, or a host of the SSH configuration; null for this computer.</param>
/// <param name="OwnerName">The name of the owner.</param>
/// <param name="OrganizationName">The name of the organization; null for the server's default.</param>
/// <param name="Address">The address the machines reach it at (a domain, a Tailscale name) when something stands in front of it; null for the local network.</param>
/// <param name="Port">Its port; null for 2586.</param>
/// <param name="DataRoot">Its data folder there; null for <c>~/.altaserver</c>.</param>
/// <param name="ToolPath">The folder of the tool there; null for <c>~/.dotnet/tools</c>.</param>
/// <param name="PackageFolder">A folder of this computer with its packages; null for nuget.org.</param>
/// <param name="TrustedHostKey">The fingerprint of the host key the person trusted, as it was shown to them; null for the keys ssh knows.</param>
/// <param name="OwnCertificate">On the local network (no <paramref name="Address"/>), whether the server is reached over HTTPS with a certificate of its own, which every device pins; otherwise plain HTTP.</param>
/// <param name="AlwaysOn">Over SSH, whether the server starts with its machine, with nobody logged in (sudo there).</param>
/// <param name="SudoPassword">The password sudo asks there for <paramref name="AlwaysOn"/>, given once and never kept; null for none.</param>
internal sealed record FleetServerSetupRequest(string ExpectedEpoch, string? Target, string OwnerName, string? OrganizationName, string? Address, int? Port, string? DataRoot,
    string? ToolPath, string? PackageFolder, string? TrustedHostKey, bool OwnCertificate = false, bool AlwaysOn = false, string? SudoPassword = null);
