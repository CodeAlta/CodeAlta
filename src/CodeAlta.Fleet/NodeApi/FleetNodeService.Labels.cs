using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.NodeApi;

// The labels of the machine of the node (fleet/labels.json): its owner's local control, and a device that manages the
// node. The node publishes them in its inventory at its next publication, and the rules of environments read them here.
public sealed partial class FleetNodeService
{
    private void RegisterLabels(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlLabelsGet, access, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetMachineLabelsInfo,
            (_, _, _) => Task.FromResult(new FleetMachineLabelsInfo(FleetMachineLabels.Read(_host.CatalogOptions.FleetRoot))), optionalParams: true);
        On(caller, FleetNodeMethods.ControlLabelsSet, access, FleetNodeJsonContext.Default.FleetMachineLabelsInfo, FleetNodeJsonContext.Default.FleetMachineLabelsInfo,
            SetLabelsAsync);
    }

    private Task<FleetMachineLabelsInfo> SetLabelsAsync(FleetNodeCall call, FleetMachineLabelsInfo parameters, CancellationToken cancellationToken)
    {
        if (parameters.Labels is null) throw new FleetException(FleetErrorCodes.InvalidParams, null, "The request names the labels of the machine.");
        IReadOnlyList<string> kept;
        try
        {
            kept = FleetMachineLabels.Write(_host.CatalogOptions.FleetRoot, parameters.Labels);
        }
        catch (ArgumentException exception)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message.Split(" (Parameter", 2)[0]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetErrorCodes.InternalError, FleetErrorHints.Retry, $"The labels could not be written: {exception.Message}");
        }

        LogInfo($"The labels of the machine changed ({(call.Caller.IsOwner ? "the owner" : call.Caller.DeviceId)}): [{string.Join(", ", kept)}].");
        return Task.FromResult(new FleetMachineLabelsInfo(kept));
    }
}
