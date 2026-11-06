using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The permission mode the sessions of a machine this computer manages run in when nothing else chooses it
/// (<c>doc/fleet/node.md</c>, "Safety"), on its page, as <c>altanode permission-mode</c> sets it there: bypass
/// permissions, the default, or ask first.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Reads the permission mode the sessions of a machine run in by default.</summary>
    [NeoRpcMethod("sessionDefaults")]
    public async Task<MachineSessionDefaultsView> SessionDefaultsAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var defaults = await host.ManageAsync((client, token) => client.GetSessionDefaultsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", null, defaults.PermissionMode);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(Code(exception), exception.Error.Message, null); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, null); }
    }

    /// <summary>Sets the permission mode the sessions of a machine run in by default.</summary>
    [NeoRpcMethod("setSessionPermissionMode")]
    public Task<MachineManageResult> SetSessionPermissionModeAsync(MachineSessionPermissionModeRequest request, CancellationToken cancellationToken)
    {
        if (!FleetSessionDefaults.TryParsePermissionMode(request?.PermissionMode, out var mode))
            return Invalid(request?.ExpectedEpoch, "A machine runs its sessions in bypassPermissions or default (ask first).");
        return ManageAsync(request!.ExpectedEpoch, request.MachineId, (client, token) => client.SetSessionDefaultsAsync(new FleetSessionDefaultsInfo(mode), token),
            cancellationToken);
    }
}

/// <summary>The permission mode the sessions of a machine run in by default, as the person who manages it sees it.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered (<c>method_not_found</c> for a machine of an earlier version).</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="PermissionMode"><c>bypassPermissions</c> or <c>default</c>; null when it could not be read.</param>
internal sealed record MachineSessionDefaultsView(string Status, string? Message, string? PermissionMode);

/// <summary>The permission mode to give the sessions of a machine by default.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="PermissionMode"><c>bypassPermissions</c> or <c>default</c>.</param>
internal sealed record MachineSessionPermissionModeRequest(string ExpectedEpoch, string MachineId, string PermissionMode);
