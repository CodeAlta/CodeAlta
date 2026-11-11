using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// The providers of the node (doc/fleet/node.md, credentials on a node): its owner's local control, and a device paired with
// the node that manages it. Each says whether it is ready and why not; a provider is added or changed with its key, which
// goes over the end-to-end channel into the secrets store of the machine (never in clear in its configuration); and the
// device-code sign-in of the providers that have one runs on the node while the desktop shows the code. A key or a sign-in
// is a credential of the machine: as for its secrets, a device trusted only through the list of an organization never sets one.
public sealed partial class FleetNodeService
{
    private const int MaxProviderKeyLength = 64;
    private const int MaxProviderTextLength = 2048;
    private const int MaxProviderApiKeyLength = 16 * 1024;

    private void RegisterProviders(FleetNodeCaller caller, Access read, Access change)
    {
        On(caller, FleetNodeMethods.ControlProvidersList, read, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetProviderList, ControlListProvidersAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlProvidersSet, change, FleetNodeJsonContext.Default.FleetProviderSetParams,
            FleetNodeJsonContext.Default.FleetProviderStatus, ControlSetProviderAsync);
        On(caller, FleetNodeMethods.ControlProvidersSignIn, change, FleetNodeJsonContext.Default.FleetProviderRef,
            FleetNodeJsonContext.Default.FleetProviderSignInPrompt, ControlStartProviderSignInAsync);
        On(caller, FleetNodeMethods.ControlProvidersSignInWait, change, FleetNodeJsonContext.Default.FleetProviderSignInWaitParams,
            FleetNodeJsonContext.Default.FleetProviderSignInState, ControlWaitProviderSignInAsync);
        On(caller, FleetNodeMethods.ControlProvidersOffers, read, FleetNodeJsonContext.Default.FleetProviderOffersParams,
            FleetNodeJsonContext.Default.FleetProviderOfferList, ControlListProviderOffersAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlProvidersAdd, change, FleetNodeJsonContext.Default.FleetProviderAddParams,
            FleetNodeJsonContext.Default.FleetProviderOperationState, ControlAddProviderAsync);
        On(caller, FleetNodeMethods.ControlProvidersUpdate, change, FleetNodeJsonContext.Default.FleetProviderRef,
            FleetNodeJsonContext.Default.FleetProviderOperationState, ControlUpdateProviderAsync);
        On(caller, FleetNodeMethods.ControlProvidersOperationWait, change, FleetNodeJsonContext.Default.FleetProviderOperationWaitParams,
            FleetNodeJsonContext.Default.FleetProviderOperationState, ControlWaitProviderOperationAsync);
    }

    private Task<FleetProviderOfferList> ControlListProviderOffersAsync(FleetNodeCall call, FleetProviderOffersParams parameters, CancellationToken cancellationToken)
        => Providers.ListOffersAsync(parameters.Refresh, cancellationToken);

    private async Task<FleetProviderOperationState> ControlAddProviderAsync(FleetNodeCall call, FleetProviderAddParams parameters, CancellationToken cancellationToken)
    {
        if (parameters.OfferId is not { Length: > 0 and <= 256 } || parameters.Key is { } key && !IsProviderKey(key))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The offer is not valid: an identifier of at most 256 characters, and a key of letters, digits, - and _.");
        var setBy = SecretsActor(call);
        try
        {
            var state = await Providers.StartAddAsync(parameters, setBy, cancellationToken).ConfigureAwait(false);
            LogInfo($"{setBy} adds the provider of the offer {parameters.OfferId}.");
            return state;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private async Task<FleetProviderOperationState> ControlUpdateProviderAsync(FleetNodeCall call, FleetProviderRef parameters, CancellationToken cancellationToken)
    {
        if (!IsProviderKey(parameters.Key)) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A provider key is letters, digits, - and _.");
        try
        {
            var state = await Providers.StartUpdateAsync(parameters.Key, cancellationToken).ConfigureAwait(false);
            LogInfo($"{SecretsActor(call)} updates the provider {parameters.Key}.");
            return state;
        }
        catch (KeyNotFoundException exception)
        {
            throw new FleetException(FleetNodeErrorCodes.ProviderNotFound, null, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private Task<FleetProviderOperationState> ControlWaitProviderOperationAsync(FleetNodeCall call, FleetProviderOperationWaitParams parameters, CancellationToken cancellationToken)
    {
        if (parameters.Id is not { Length: > 0 and <= 64 }) throw new FleetException(FleetErrorCodes.InvalidParams, null, "An operation is named by an identifier of at most 64 characters.");
        return Providers.WaitOperationAsync(parameters.Id, TimeSpan.FromSeconds(Math.Clamp(parameters.WaitSeconds, 0, 25)), cancellationToken);
    }

    private IFleetNodeProviders Providers => _options.Providers
        ?? throw new FleetException(FleetNodeErrorCodes.ProvidersUnavailable, null, "This node cannot change its providers.");

    private Task<FleetProviderList> ControlListProvidersAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => Providers.ListAsync(cancellationToken);

    private async Task<FleetProviderStatus> ControlSetProviderAsync(FleetNodeCall call, FleetProviderSetParams parameters, CancellationToken cancellationToken)
    {
        if (!IsProviderKey(parameters.Key) || parameters.Type is { Length: > MaxProviderKeyLength } || parameters.DisplayName is { Length: > MaxProviderTextLength }
            || parameters.Model is { Length: > MaxProviderTextLength } || parameters.ApiUrl is { Length: > MaxProviderTextLength } || parameters.ApiKey is { Length: > MaxProviderApiKeyLength })
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The provider is not valid: a key of letters, digits, - and _, and values of a bounded length.");
        var setBy = SecretsActor(call);
        FleetProviderStatus status;
        try
        {
            status = await Providers.SetAsync(parameters, setBy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // What the configuration of the node refuses (a value, a provider without what its type needs) is said as it is.
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message.Split(" (Parameter", 2)[0]);
        }
        catch (Exception exception) when (exception is Catalog.Secrets.SecretStoreUnavailableException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new FleetException(FleetNodeErrorCodes.SecretsUnavailable, null, exception.Message);
        }

        // The sessions of the node mask a new key at once.
        if (parameters.ApiKey is { Length: > 0 }) _host.AgentHub.SecretMasker?.Refresh();
        // The value itself is never logged: only that it was given.
        LogInfo($"{setBy} set the provider {parameters.Key}{(parameters.ApiKey is { Length: > 0 } ? " and its key" : "")}.");
        return status;
    }

    private async Task<FleetProviderSignInPrompt> ControlStartProviderSignInAsync(FleetNodeCall call, FleetProviderRef parameters, CancellationToken cancellationToken)
    {
        if (!IsProviderKey(parameters.Key)) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A provider key is letters, digits, - and _.");
        try
        {
            var prompt = await Providers.StartSignInAsync(parameters.Key, cancellationToken).ConfigureAwait(false);
            LogInfo($"{SecretsActor(call)} started the sign-in of the provider {parameters.Key}.");
            return prompt;
        }
        catch (KeyNotFoundException exception)
        {
            throw new FleetException(FleetNodeErrorCodes.ProviderNotFound, null, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            throw new FleetException(FleetNodeErrorCodes.ProviderSignInUnsupported, null, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            throw new FleetException(FleetNodeErrorCodes.ProviderSignInFailed, null, exception.Message);
        }
    }

    private Task<FleetProviderSignInState> ControlWaitProviderSignInAsync(FleetNodeCall call, FleetProviderSignInWaitParams parameters, CancellationToken cancellationToken)
    {
        if (!IsProviderKey(parameters.Key)) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A provider key is letters, digits, - and _.");
        return Providers.WaitSignInAsync(parameters.Key, TimeSpan.FromSeconds(Math.Clamp(parameters.WaitSeconds, 0, 25)), cancellationToken);
    }

    /// <summary>Whether a text can be the key of a provider: 1 to 64 letters, digits, <c>-</c> and <c>_</c>.</summary>
    /// <param name="key">The text.</param>
    /// <returns>Whether it can be.</returns>
    public static bool IsProviderKey(string? key)
        => key is { Length: > 0 and <= MaxProviderKeyLength } && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
