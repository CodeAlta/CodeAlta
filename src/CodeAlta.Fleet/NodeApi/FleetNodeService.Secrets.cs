using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

// The secrets and variables of the machine of the node (~/.alta/secrets): its owner's local control, and a device paired
// with the node that manages it (DevicePermissions.Manage, given by the owner). A device trusted only through the device
// list of an organization never does, whatever the list says. A secret is set, rotated and deleted, and listed by name:
// its value never leaves the machine.
public sealed partial class FleetNodeService
{
    private void RegisterSecrets(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlSecretsList, access, FleetNodeJsonContext.Default.FleetEmpty,
            FleetNodeJsonContext.Default.FleetSecretList, ControlListSecretsAsync, optionalParams: true);
        On(caller, FleetNodeMethods.ControlSecretsSet, access, FleetNodeJsonContext.Default.FleetSecretSetParams,
            FleetNodeJsonContext.Default.FleetSecretChange, (call, parameters, token) => ControlSetSecretAsync(call, parameters, rotate: false));
        On(caller, FleetNodeMethods.ControlSecretsRotate, access, FleetNodeJsonContext.Default.FleetSecretSetParams,
            FleetNodeJsonContext.Default.FleetSecretChange, (call, parameters, token) => ControlSetSecretAsync(call, parameters, rotate: true));
        On(caller, FleetNodeMethods.ControlSecretsDelete, access, FleetNodeJsonContext.Default.FleetSecretRef,
            FleetNodeJsonContext.Default.FleetSecretChange, ControlDeleteSecretAsync);
    }

    // Who changed a value, as the store records it and the log says it: the owner on the machine, or the managing device by its name.
    private string SecretsActor(FleetNodeCall call) => call.Device is { } device ? $"{device.Name} (manage)" : $"{_options.Name} owner";

    private LocalSecretStore Secrets => new(_host.CatalogOptions.GlobalRoot);

    private Task<FleetSecretList> ControlListSecretsAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => Task.FromResult(OnSecrets(store => new FleetSecretList(
            [.. store.ListSecrets().Select(static secret => new FleetSecretInfo(secret.Name, secret.Version, secret.SetBy, secret.SetAt, null))],
            [.. store.ListVariables().Select(static variable => new FleetSecretInfo(variable.Name, variable.Version, variable.SetBy, variable.SetAt, variable.Value))],
            store.Protection)));

    private Task<FleetSecretChange> ControlSetSecretAsync(FleetNodeCall call, FleetSecretSetParams parameters, bool rotate)
    {
        if (rotate && parameters.Variable) throw new FleetException(FleetErrorCodes.InvalidParams, null, "A variable is set, not rotated.");
        var setBy = SecretsActor(call);
        var change = OnSecrets(store =>
        {
            if (parameters.Variable)
            {
                var variable = store.SetVariable(parameters.Name, parameters.Value, setBy);
                return new FleetSecretChange(new(variable.Name, variable.Version, variable.SetBy, variable.SetAt, variable.Value), true);
            }

            var secret = rotate ? store.RotateSecret(parameters.Name, parameters.Value, setBy) : store.SetSecret(parameters.Name, parameters.Value, setBy);
            return new FleetSecretChange(new(secret.Name, secret.Version, secret.SetBy, secret.SetAt, null), true);
        });
        // The sessions of the node mask the new value at once.
        _host.AgentHub.SecretMasker?.Refresh();
        LogInfo($"{setBy} {(rotate ? "rotated" : "set")} the {(parameters.Variable ? "variable" : "secret")} {parameters.Name}.");
        return Task.FromResult(change);
    }

    private Task<FleetSecretChange> ControlDeleteSecretAsync(FleetNodeCall call, FleetSecretRef parameters, CancellationToken cancellationToken)
    {
        var deleted = OnSecrets(store => parameters.Variable ? store.DeleteVariable(parameters.Name) : store.DeleteSecret(parameters.Name));
        if (!deleted) throw new FleetException(FleetNodeErrorCodes.SecretNotFound, null, $"No {(parameters.Variable ? "variable" : "secret")} is named {parameters.Name} on this machine.");
        _host.AgentHub.SecretMasker?.Refresh();
        LogInfo($"{SecretsActor(call)} deleted the {(parameters.Variable ? "variable" : "secret")} {parameters.Name}.");
        return Task.FromResult(new FleetSecretChange(null, true));
    }

    // A call on the store, its failures said as errors of the node API.
    private T OnSecrets<T>(Func<LocalSecretStore, T> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return call(Secrets);
        }
        catch (ArgumentException exception)
        {
            throw new FleetException(FleetErrorCodes.InvalidParams, null, exception.Message.Split(" (Parameter", 2)[0]);
        }
        catch (KeyNotFoundException exception)
        {
            throw new FleetException(FleetNodeErrorCodes.SecretNotFound, null, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SecretStoreUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetNodeErrorCodes.SecretsUnavailable, null, exception.Message);
        }
    }
}
