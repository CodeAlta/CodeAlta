using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Add a machine this computer manages to its server: this computer makes a one-time enrollment code, the machine
/// enrolls with it from itself (<c>control.servers.join</c>), and this computer approves the device the machine named
/// over the channel pinned to its key — the person checks nothing by hand, and the six words are shown.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Gets the address of a server this computer is linked to, by the id of its link; null when unknown.</summary>
    internal Func<string, string?>? ServerAddress { get; set; }

    /// <summary>Gets what makes a one-time enrollment code at a server, by the id of its link.</summary>
    internal Func<string, CancellationToken, Task<string>>? CreateJoinCode { get; set; }

    /// <summary>Gets what approves a device at a server, by the id of its link and the device.</summary>
    internal Func<string, string, CancellationToken, Task>? ApproveDevice { get; set; }

    /// <summary>Has a machine this computer manages join a server this computer owns, and approves it there.</summary>
    [NeoRpcMethod("joinServer")]
    public async Task<MachineJoinServerResult> JoinServerAsync(MachineJoinServerRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.LinkId is not { Length: > 0 and <= 64 } linkId) return new("invalid_request", null, null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null, null);
        if (ServerAddress?.Invoke(linkId) is not { } url || CreateJoinCode is not { } code || ApproveDevice is not { } approve) return new("server_not_found", null, null, null);
        FleetServerJoinResult joined;
        try
        {
            var token = await code(linkId, cancellationToken).ConfigureAwait(false);
            joined = await host.ManageAsync((client, cancellation) => client.JoinServerAsync(new FleetServerJoinParams(url, token, host.Machine.Name), cancellation),
                changes: true, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null, null); }
        catch (FleetException exception) { return new(Code(exception), null, null, exception.Error.Message); }
        catch (FleetServerException exception) { return new("server_refused", null, null, exception.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException or HttpRequestException) { return new(RemoteSessionHost.MachineOfflineCode, null, null, exception.Message); }

        try
        {
            await approve(linkId, joined.DeviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (FleetServerException exception) { return new("approve_failed", joined.Name, joined.Words, exception.Message); }
        catch (HttpRequestException exception) { return new("approve_failed", joined.Name, joined.Words, exception.Message); }

        Changed();
        return new("ok", joined.Name, joined.Words, null);
    }
}

/// <summary>A machine to add to a server.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine, which this computer manages.</param>
/// <param name="LinkId">The server, by the id of this computer's link to it.</param>
internal sealed record MachineJoinServerRequest(string ExpectedEpoch, string MachineId, string LinkId);

/// <summary>The outcome of adding a machine to a server.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, <c>server_refused</c>, <c>approve_failed</c>, or the code the machine answered.</param>
/// <param name="Name">The name of the machine at the server.</param>
/// <param name="Words">The six words of its key.</param>
/// <param name="Message">What went wrong.</param>
internal sealed record MachineJoinServerResult(string Status, string? Name, string? Words, string? Message);
