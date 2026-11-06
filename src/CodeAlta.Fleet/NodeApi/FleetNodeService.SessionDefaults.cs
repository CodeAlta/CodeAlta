using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// What the sessions of the node run with when nothing else chooses it (fleet/session-defaults.json): its owner's local
// control, and a device that manages the node. The host reads it again for each session and each request.
public sealed partial class FleetNodeService
{
    private void RegisterSessionDefaults(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlSessionDefaultsGet, access, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetSessionDefaultsInfo,
            (_, _, _) => Task.FromResult(FleetSessionDefaults.Read(_host.CatalogOptions.FleetRoot)), optionalParams: true);
        On(caller, FleetNodeMethods.ControlSessionDefaultsSet, access, FleetNodeJsonContext.Default.FleetSessionDefaultsInfo, FleetNodeJsonContext.Default.FleetSessionDefaultsInfo,
            SetSessionDefaultsAsync);
    }

    private Task<FleetSessionDefaultsInfo> SetSessionDefaultsAsync(FleetNodeCall call, FleetSessionDefaultsInfo parameters, CancellationToken cancellationToken)
    {
        if (!FleetSessionDefaults.TryParsePermissionMode(parameters.PermissionMode, out _))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A machine runs its sessions in bypassPermissions or default (ask first) by default.");
        FleetSessionDefaultsInfo kept;
        try
        {
            kept = FleetSessionDefaults.Write(_host.CatalogOptions.FleetRoot, parameters);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetErrorCodes.InternalError, FleetErrorHints.Retry, $"The defaults of the sessions could not be written: {exception.Message}");
        }

        LogInfo($"The permission mode of the sessions of the machine changed ({(call.Caller.IsOwner ? "the owner" : call.Caller.DeviceId)}): {kept.PermissionMode}.");
        return Task.FromResult(kept);
    }
}
