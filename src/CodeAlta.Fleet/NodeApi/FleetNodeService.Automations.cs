using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>The <c>automations.*</c> methods: the automations a paired device gives the node to run (<see cref="IFleetNodeAutomations"/>).</summary>
public sealed partial class FleetNodeService
{
    private const int DefaultRunLimit = 50;

    private void RegisterAutomations(FleetNodeCaller caller)
    {
        // Giving an automation and running one start sessions later or now: the device may start sessions, and give automations.
        On(caller, FleetNodeMethods.AutomationsPut, Access.Automate, FleetNodeJsonContext.Default.FleetAutomationPutParams,
            FleetNodeJsonContext.Default.FleetAutomationInfo, PutAutomationAsync);
        On(caller, FleetNodeMethods.AutomationsRun, Access.Automate, FleetNodeJsonContext.Default.FleetAutomationRef,
            FleetNodeJsonContext.Default.FleetAutomationRunInfo, RunAutomationAsync);
        On(caller, FleetNodeMethods.AutomationsTry, Access.Automate, FleetNodeJsonContext.Default.FleetAutomationRef,
            FleetNodeJsonContext.Default.FleetAutomationRunInfo, TryAutomationAsync);
        On(caller, FleetNodeMethods.AutomationsRemove, Access.Automations, FleetNodeJsonContext.Default.FleetAutomationRef,
            FleetNodeJsonContext.Default.FleetAutomationRemoved, RemoveAutomationAsync);
        On(caller, FleetNodeMethods.AutomationsList, Access.Automations, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetAutomationList, ListAutomationsAsync, optionalParams: true);
        On(caller, FleetNodeMethods.AutomationsRuns, Access.Automations, FleetNodeJsonContext.Default.FleetAutomationRunsParams,
            FleetNodeJsonContext.Default.FleetAutomationRunList, ListAutomationRunsAsync, optionalParams: true);
    }

    private Task<FleetAutomationInfo> PutAutomationAsync(FleetNodeCall call, FleetAutomationPutParams parameters, CancellationToken cancellationToken)
    {
        var automations = RequireAutomations();
        return Once(call, FleetNodeJsonContext.Default.FleetAutomationInfo, () => automations.PutAsync(AutomationCaller(call), parameters, cancellationToken));
    }

    private Task<FleetAutomationRunInfo> RunAutomationAsync(FleetNodeCall call, FleetAutomationRef parameters, CancellationToken cancellationToken)
    {
        var automations = RequireAutomations();
        RequireWork();
        return Once(call, FleetNodeJsonContext.Default.FleetAutomationRunInfo, () => automations.RunAsync(AutomationCaller(call), parameters.Id, parameters.DryRun == true, cancellationToken));
    }

    private Task<FleetAutomationRunInfo> TryAutomationAsync(FleetNodeCall call, FleetAutomationRef parameters, CancellationToken cancellationToken)
    {
        var automations = RequireAutomations();
        RequireWork();
        return Once(call, FleetNodeJsonContext.Default.FleetAutomationRunInfo, () => automations.RunAsync(AutomationCaller(call), parameters.Id, true, cancellationToken));
    }

    private Task<FleetAutomationRemoved> RemoveAutomationAsync(FleetNodeCall call, FleetAutomationRef parameters, CancellationToken cancellationToken)
    {
        var automations = RequireAutomations();
        return Once(call, FleetNodeJsonContext.Default.FleetAutomationRemoved, async () =>
            new FleetAutomationRemoved(parameters.Id, await automations.RemoveAsync(AutomationCaller(call), parameters.Id, cancellationToken).ConfigureAwait(false)));
    }

    private Task<FleetAutomationList> ListAutomationsAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => Task.FromResult(new FleetAutomationList(RequireAutomations().List(AutomationCaller(call))));

    private Task<FleetAutomationRunList> ListAutomationRunsAsync(FleetNodeCall call, FleetAutomationRunsParams parameters, CancellationToken cancellationToken)
        => Task.FromResult(new FleetAutomationRunList(RequireAutomations().Runs(AutomationCaller(call), parameters.Id, parameters.Limit ?? DefaultRunLimit)));

    private IFleetNodeAutomations RequireAutomations()
        => _options.Automations ?? throw new FleetException(FleetNodeErrorCodes.AutomationsUnavailable, null, "This machine runs no automation for other devices.");

    private static FleetAutomationCaller AutomationCaller(FleetNodeCall call)
        => new(call.Caller.DeviceId, call.Device?.Name ?? call.Caller.DeviceId) { Fingerprint = call.Caller.Fingerprint.ToString() };
}
