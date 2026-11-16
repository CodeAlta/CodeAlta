using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// The page of the phones of the node (its Mobile plugin): its owner's local control, and a device paired to manage the node.
// A phone gets what the owner of the profile has, with no person attached to its key: a machine that lets other people in
// does not serve the page, and who shows a pairing code holds the sessions of the machine, so a device trusted through an
// organization alone never does.
public sealed partial class FleetNodeService
{
    /// <summary>What a machine that lets other people in answers to a request to serve the page of the phones.</summary>
    public const string MobileSharedMachine = "This machine lets other people in: a phone would see the sessions of all of them, so it does not serve the page of the phones.";

    private void RegisterMobile(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlMobileState, access, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetMobileState,
            MobileStateAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlMobileEnable, access, FleetNodeJsonContext.Default.FleetMobileEnableParams, FleetNodeJsonContext.Default.FleetMobileState,
            MobileEnableAsync);
        On(caller, FleetNodeMethods.ControlMobileInvoke, access, FleetNodeJsonContext.Default.FleetMobileInvokeParams, FleetNodeJsonContext.Default.FleetMobileInvokeResult,
            MobileInvokeAsync);
    }

    /// <summary>
    /// Gets whether the machine lets people other than its owner in: a person it names, a device that sees only the
    /// sessions of its own person, or a machine an organization gave to a team or keeps for itself
    /// (<see cref="FleetNodeServiceOptions.IsSharedMachine"/>).
    /// </summary>
    public bool LetsOtherPeopleIn
        => People.Read().People.Count > 0 || _trusted.List().Any(static device => device.OwnSessionsOnly) || _options.IsSharedMachine?.Invoke() == true;

    private IFleetNodeMobile Mobile => _options.Mobile ?? throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "This machine does not serve the page of the phones.");

    private async Task<FleetMobileState> MobileStateAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var state = await Mobile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return LetsOtherPeopleIn ? state with { Refusal = MobileSharedMachine } : state;
    }

    private async Task<FleetMobileState> MobileEnableAsync(FleetNodeCall call, FleetMobileEnableParams parameters, CancellationToken cancellationToken)
    {
        var mobile = Mobile;
        if (parameters.Enabled && LetsOtherPeopleIn) throw new FleetException(FleetNodeErrorCodes.PolicyRefused, null, MobileSharedMachine);
        var state = await mobile.EnableAsync(parameters.Enabled, cancellationToken).ConfigureAwait(false);
        LogInfo($"{Capitalized(Who(call))} turned the page of the phones {(parameters.Enabled ? "on" : "off")}.");
        return state;
    }

    private async Task<FleetMobileInvokeResult> MobileInvokeAsync(FleetNodeCall call, FleetMobileInvokeParams parameters, CancellationToken cancellationToken)
    {
        var mobile = Mobile;
        if (parameters.Method is null || !FleetMobileInvokeParams.Methods.Contains(parameters.Method, StringComparer.Ordinal))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The page of the phones has no such call.");
        if (parameters.Payload is { Length: > FleetMobileInvokeParams.MaxPayload })
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The parameters of the call are too long.");
        // A machine that took a person in since the page was turned on stops serving it, and gives no code.
        if (LetsOtherPeopleIn)
        {
            await StopMobileForOthersAsync().ConfigureAwait(false);
            throw new FleetException(FleetNodeErrorCodes.PolicyRefused, null, MobileSharedMachine);
        }

        string? json;
        try
        {
            json = await mobile.InvokeAsync(parameters.Method, parameters.Payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message.Split(" (Parameter", 2)[0]);
        }

        if (json is null) throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "The page of the phones is turned off on this machine.");
        // Who was given a pairing code, or disconnected the phones, is in the log; the code never is.
        if (parameters.Method is "mobile.pair" or "mobile.reset" or "mobile.settings.set") LogInfo($"{Capitalized(Who(call))} called {parameters.Method} of the page of the phones.");
        return new FleetMobileInvokeResult(json);
    }

    /// <summary>
    /// Stops the page of the phones when the machine lets other people in: called when a person is let in, and before any
    /// call of the page, so that a machine that became shared serves no phone.
    /// </summary>
    /// <returns>A task that completes once the page is off, or at once when the machine has none or is not shared.</returns>
    public async Task StopMobileForOthersAsync()
    {
        if (_options.Mobile is not { } mobile || !LetsOtherPeopleIn) return;
        try
        {
            var state = await mobile.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (!state.Enabled && !state.Running) return;
            await mobile.EnableAsync(false, CancellationToken.None).ConfigureAwait(false);
            LogInfo("The page of the phones was turned off: this machine now lets other people in.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LogInfo("The page of the phones could not be turned off: " + exception.Message);
        }
    }
}
