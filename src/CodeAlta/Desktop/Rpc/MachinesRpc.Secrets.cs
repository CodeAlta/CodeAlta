using CodeAlta.Catalog.Secrets;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The secrets and variables of a machine this computer manages, on its page: listed by name, set, rotated and deleted
/// there as Settings &gt; Secrets does here. A value goes to the machine and never comes back; the machine checks at each
/// call that this computer is paired with it and manages it.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Lists the secrets of a machine by name, and its variables.</summary>
    [NeoRpcMethod("secrets")]
    public async Task<SecretsList> SecretsAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], []);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return new("invalid_request", null, [], []);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, [], []);
        try
        {
            var list = await host.ManageAsync((client, token) => client.ListSecretsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", list.Protection,
                [.. list.Secrets.OrderBy(static secret => secret.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static secret => new SecretView(secret.Name, secret.Version, secret.SetBy, Format(secret.SetAt)))],
                [.. list.Variables.OrderBy(static variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static variable => new VariableView(variable.Name, variable.Value ?? "", variable.Version, variable.SetBy, Format(variable.SetAt)))]);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, [], []); }
        catch (FleetException exception) { return new(Code(exception), null, [], []); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, [], []); }
    }

    /// <summary>Adds a secret to a machine, or gives it a new value and a new version.</summary>
    [NeoRpcMethod("setSecret")]
    public Task<MachineManageResult> SetSecretAsync(MachineSecretWriteRequest request, CancellationToken cancellationToken)
        => WriteSecretAsync(request, variable: false, rotate: false, cancellationToken);

    /// <summary>Gives a secret that is set on a machine a new value and a new version.</summary>
    [NeoRpcMethod("rotateSecret")]
    public Task<MachineManageResult> RotateSecretAsync(MachineSecretWriteRequest request, CancellationToken cancellationToken)
        => WriteSecretAsync(request, variable: false, rotate: true, cancellationToken);

    /// <summary>Adds a variable to a machine, or changes its value.</summary>
    [NeoRpcMethod("setVariable")]
    public Task<MachineManageResult> SetVariableAsync(MachineSecretWriteRequest request, CancellationToken cancellationToken)
        => WriteSecretAsync(request, variable: true, rotate: false, cancellationToken);

    /// <summary>Deletes a secret of a machine.</summary>
    [NeoRpcMethod("deleteSecret")]
    public Task<MachineManageResult> DeleteSecretAsync(MachineSecretNameRequest request, CancellationToken cancellationToken)
        => DeleteSecretAsync(request, variable: false, cancellationToken);

    /// <summary>Deletes a variable of a machine.</summary>
    [NeoRpcMethod("deleteVariable")]
    public Task<MachineManageResult> DeleteVariableAsync(MachineSecretNameRequest request, CancellationToken cancellationToken)
        => DeleteSecretAsync(request, variable: true, cancellationToken);

    private Task<MachineManageResult> WriteSecretAsync(MachineSecretWriteRequest? request, bool variable, bool rotate, CancellationToken cancellationToken)
    {
        if (SecretName(request?.Name) is not { } name) return Invalid(request?.ExpectedEpoch, "A name is letters, digits and _, not starting with a digit, as the name of an environment variable.");
        if (string.IsNullOrEmpty(request!.Value)) return Invalid(request.ExpectedEpoch, "The value is empty.");
        if (request.Value.Length > LocalSecretStore.MaximumValueLength) return Invalid(request.ExpectedEpoch, "The value is too large.");
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.SetSecretAsync(new FleetSecretSetParams(name, request.Value, variable), rotate, token),
            cancellationToken);
    }

    private Task<MachineManageResult> DeleteSecretAsync(MachineSecretNameRequest? request, bool variable, CancellationToken cancellationToken)
    {
        if (SecretName(request?.Name) is not { } name) return Invalid(request?.ExpectedEpoch, "A name is letters, digits and _, not starting with a digit, as the name of an environment variable.");
        return ManageAsync(request!.ExpectedEpoch, request.MachineId, (client, token) => client.DeleteSecretAsync(new FleetSecretRef(name, variable), token), cancellationToken);
    }

    private Task<MachineManageResult> Invalid(string? expectedEpoch, string message)
        => Task.FromResult(Refuse(expectedEpoch) is { } refused ? new MachineManageResult(refused, null) : new MachineManageResult(FleetErrorCodes.InvalidParams, message));

    private static string? SecretName(string? name) => name?.Trim() is { Length: > 0 and <= 128 } trimmed && LocalSecretStore.IsName(trimmed) ? trimmed : null;
}

/// <summary>A secret or a variable of a machine to write.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Name">The name.</param>
/// <param name="Value">The value, which goes to the machine and never comes back for a secret.</param>
internal sealed record MachineSecretWriteRequest(string ExpectedEpoch, string MachineId, string Name, string Value);

/// <summary>A secret or a variable of a machine to delete.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Name">The name.</param>
internal sealed record MachineSecretNameRequest(string ExpectedEpoch, string MachineId, string Name);
