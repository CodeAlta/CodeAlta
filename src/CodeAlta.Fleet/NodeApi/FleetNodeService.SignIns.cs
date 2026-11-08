using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// The sign-ins the node keeps for itself (doc/fleet/node.md, credentials on a node), made from the desktop that manages it: the
// node starts each one and keeps its secrets (the code verifier, the tokens); the desktop shows a code, or opens a page in its own
// browser and forwards the callback. A sign-in is a credential of the machine: as for its secrets and providers, a device trusted
// only through the list of an organization never makes one.
public sealed partial class FleetNodeService
{
    private const int MaxSignInKeyLength = 128;
    private const int MaxSignInCallbackLength = 8192;
    private const int MaxSignInCodeLength = 4096;

    private void RegisterSignIns(FleetNodeCaller caller, Access read, Access change)
    {
        On(caller, FleetNodeMethods.ControlSignInsList, read, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetMachineSignInList, ControlListSignInsAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlSignInsStart, change, FleetNodeJsonContext.Default.FleetMachineSignInStartParams,
            FleetNodeJsonContext.Default.FleetMachineSignInPrompt, ControlStartSignInAsync);
        On(caller, FleetNodeMethods.ControlSignInsComplete, change, FleetNodeJsonContext.Default.FleetMachineSignInCompleteParams,
            FleetNodeJsonContext.Default.FleetMachineSignInState, ControlCompleteSignInAsync);
        On(caller, FleetNodeMethods.ControlSignInsWait, change, FleetNodeJsonContext.Default.FleetMachineSignInWaitParams,
            FleetNodeJsonContext.Default.FleetMachineSignInState, ControlWaitSignInAsync);
        On(caller, FleetNodeMethods.ControlSignInsCancel, change, FleetNodeJsonContext.Default.FleetMachineSignInAttemptRef,
            FleetNodeJsonContext.Default.FleetMachineSignInState, ControlCancelSignInAsync);
        On(caller, FleetNodeMethods.ControlSignInsSignOut, change, FleetNodeJsonContext.Default.FleetMachineSignInTarget,
            FleetNodeJsonContext.Default.FleetMachineSignInItem, ControlSignOutAsync);
    }

    private IFleetNodeSignIns SignIns => _options.SignIns
        ?? throw new FleetException(FleetNodeErrorCodes.SignInsUnavailable, null, "This node does not sign in from another computer.");

    private Task<FleetMachineSignInList> ControlListSignInsAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => SignIns.ListAsync(cancellationToken);

    private async Task<FleetMachineSignInPrompt> ControlStartSignInAsync(FleetNodeCall call, FleetMachineSignInStartParams parameters, CancellationToken cancellationToken)
    {
        RequireSignInTarget(parameters.Kind, parameters.Key, parameters.Space);
        if (parameters.Way is { } way && way is not (FleetMachineSignInWays.Browser or FleetMachineSignInWays.Device or FleetMachineSignInWays.Code or FleetMachineSignInWays.Page))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A way of signing in is browser, device, code or page.");
        try
        {
            var prompt = await SignIns.StartAsync(parameters, cancellationToken).ConfigureAwait(false);
            LogInfo($"{SecretsActor(call)} started the sign-in of the {parameters.Kind} {parameters.Key}{(parameters.Space is { } space ? $" of the space {space}" : "")} ({prompt.Way}).");
            return prompt;
        }
        catch (KeyNotFoundException exception) { throw new FleetException(FleetNodeErrorCodes.SignInTargetNotFound, null, exception.Message); }
        catch (NotSupportedException exception) { throw new FleetException(FleetNodeErrorCodes.SignInUnsupported, null, exception.Message); }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetNodeErrorCodes.SignInFailed, null, exception.Message);
        }
    }

    private async Task<FleetMachineSignInState> ControlCompleteSignInAsync(FleetNodeCall call, FleetMachineSignInCompleteParams parameters, CancellationToken cancellationToken)
    {
        RequireAttempt(parameters.AttemptId);
        if (parameters.Callback is { Length: > MaxSignInCallbackLength } || parameters.Code is { Length: > MaxSignInCodeLength } || parameters.Callback is null && parameters.Code is null)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "A sign-in is given the callback of the browser or a code, of a bounded length.");
        try
        {
            var state = await SignIns.CompleteAsync(parameters, cancellationToken).ConfigureAwait(false);
            // What it was given is never logged: only that it was, and how it ended.
            LogInfo($"{SecretsActor(call)} completed the sign-in {parameters.AttemptId}: {state.Status}.");
            return state;
        }
        catch (ArgumentException exception) { throw new FleetException(FleetNodeErrorCodes.SignInFailed, null, exception.Message.Split(" (Parameter", 2)[0]); }
    }

    private Task<FleetMachineSignInState> ControlWaitSignInAsync(FleetNodeCall call, FleetMachineSignInWaitParams parameters, CancellationToken cancellationToken)
    {
        RequireAttempt(parameters.AttemptId);
        return SignIns.WaitAsync(parameters.AttemptId, TimeSpan.FromSeconds(Math.Clamp(parameters.WaitSeconds, 0, 25)), cancellationToken);
    }

    private async Task<FleetMachineSignInState> ControlCancelSignInAsync(FleetNodeCall call, FleetMachineSignInAttemptRef parameters, CancellationToken cancellationToken)
    {
        RequireAttempt(parameters.AttemptId);
        var state = await SignIns.CancelAsync(parameters.AttemptId).ConfigureAwait(false);
        LogInfo($"{SecretsActor(call)} cancelled the sign-in {parameters.AttemptId}.");
        return state;
    }

    private async Task<FleetMachineSignInItem> ControlSignOutAsync(FleetNodeCall call, FleetMachineSignInTarget parameters, CancellationToken cancellationToken)
    {
        RequireSignInTarget(parameters.Kind, parameters.Key, parameters.Space);
        try
        {
            var item = await SignIns.SignOutAsync(parameters, cancellationToken).ConfigureAwait(false);
            LogInfo($"{SecretsActor(call)} signed the {parameters.Kind} {parameters.Key} out.");
            return item;
        }
        catch (KeyNotFoundException exception) { throw new FleetException(FleetNodeErrorCodes.SignInTargetNotFound, null, exception.Message); }
        catch (NotSupportedException exception) { throw new FleetException(FleetNodeErrorCodes.SignInUnsupported, null, exception.Message); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetNodeErrorCodes.SignInFailed, null, exception.Message);
        }
    }

    private static void RequireSignInTarget(string? kind, string? key, string? space)
    {
        if (kind is not (FleetMachineSignInKinds.Provider or FleetMachineSignInKinds.Mcp or FleetMachineSignInKinds.Cli))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "What a machine signs in is a provider, an MCP server or a tool.");
        if (key is not { Length: > 0 and <= MaxSignInKeyLength } || key.Any(static character => char.IsControl(character) || character is '/' or '\\'))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"A key has 1 to {MaxSignInKeyLength} characters, without a separator of paths.");
        if (space is not null && !Catalog.SpaceDescriptor.IsValidId(space))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "That is not the id of a space.");
    }

    private static void RequireAttempt(string? attemptId)
    {
        if (attemptId is not { Length: > 0 and <= 64 } || !attemptId.All(char.IsAsciiLetterOrDigit))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "That is not the id of a sign-in.");
    }
}
