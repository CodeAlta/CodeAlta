using System.Buffers.Text;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// The secrets, variables, environments and approvals of the REST API (doc/fleet/secrets.md): write-only for the values,
// which travel sealed; a machine reads only the copies sealed for it.
public sealed partial class FleetServerClient
{
    /// <summary>Lists the named values of this device's person: what is known of each, and the copy sealed for this device.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretListDto> ListSecretsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/secrets", null, FleetSecretsApiJsonContext.Default.FleetSecretListDto, cancellationToken);

    /// <summary>Lists the named values this machine holds a copy of, each with its copy.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretListDto> ListHeldSecretsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/secrets/held", null, FleetSecretsApiJsonContext.Default.FleetSecretListDto, cancellationToken);

    /// <summary>Sets a named value (version 1) or rotates it (the next version), with the copies this device sealed.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="request">The value as sealed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it: not a controller device, a stale version, a name taken, a copy for a device of another person.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretDto> PutSecretAsync(string id, SetFleetSecretRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Put, "api/v1/secrets/" + Uri.EscapeDataString(id), Body(request, FleetSecretsApiJsonContext.Default.SetFleetSecretRequest),
            FleetSecretsApiJsonContext.Default.FleetSecretDto, cancellationToken);
    }

    /// <summary>Gives copies of the current version of a value to devices that hold none (a machine allowed later).</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="request">The version and the copies.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretDto> AddSecretCopiesAsync(string id, AddFleetSecretCopiesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, $"api/v1/secrets/{Uri.EscapeDataString(id)}/copies", Body(request, FleetSecretsApiJsonContext.Default.AddFleetSecretCopiesRequest),
            FleetSecretsApiJsonContext.Default.FleetSecretDto, cancellationToken);
    }

    /// <summary>Takes the copy of a value away from a device: it no longer gets it at its next run.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentException">An id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretDto> RemoveSecretCopyAsync(string id, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return SendAsync(HttpMethod.Delete, $"api/v1/secrets/{Uri.EscapeDataString(id)}/copies/{Uri.EscapeDataString(deviceId)}", null,
            FleetSecretsApiJsonContext.Default.FleetSecretDto, cancellationToken);
    }

    /// <summary>Deletes a named value and every copy of it.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values left.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretListDto> DeleteSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return SendAsync(HttpMethod.Delete, "api/v1/secrets/" + Uri.EscapeDataString(id), null, FleetSecretsApiJsonContext.Default.FleetSecretListDto, cancellationToken);
    }

    /// <summary>Lists the uses the machines published of a value, the newest first.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The uses.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretUseListDto> ListSecretUsesAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return SendAsync(HttpMethod.Get, $"api/v1/secrets/{Uri.EscapeDataString(id)}/uses", null, FleetSecretsApiJsonContext.Default.FleetSecretUseListDto, cancellationToken);
    }

    /// <summary>Publishes the uses this machine recorded of the values it holds: never a value.</summary>
    /// <param name="uses">The uses.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The uses the server took.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uses"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused them.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSecretUseListDto> PublishSecretUsesAsync(IReadOnlyList<FleetSecretUseDto> uses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uses);
        return SendAsync(HttpMethod.Post, "api/v1/secret-uses", Body(new PublishFleetSecretUsesRequest(uses), FleetSecretsApiJsonContext.Default.PublishFleetSecretUsesRequest),
            FleetSecretsApiJsonContext.Default.FleetSecretUseListDto, cancellationToken);
    }

    /// <summary>Lists the environments of this device's person, as signed.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The environments.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetEnvironmentListDto> ListEnvironmentsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/environments", null, FleetSecretsApiJsonContext.Default.FleetEnvironmentListDto, cancellationToken);

    /// <summary>Stores an environment this device signed (<see cref="SignEnvironment"/>), or a newer version of it.</summary>
    /// <param name="signed">The signed definition.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The environment as the server holds it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="ArgumentException">The definition does not read.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetEnvironmentDto> PutEnvironmentAsync(FleetSignedEnvironment signed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signed);
        if (!FleetEnvironmentDocuments.TryRead(signed.Payload, out var definition, out var reason)) throw new ArgumentException($"The environment does not hold: {reason}.", nameof(signed));
        return SendAsync(HttpMethod.Put, "api/v1/environments/" + Uri.EscapeDataString(definition.EnvironmentId),
            Body(new PutFleetEnvironmentRequest(signed), FleetSecretsApiJsonContext.Default.PutFleetEnvironmentRequest), FleetSecretsApiJsonContext.Default.FleetEnvironmentDto,
            cancellationToken);
    }

    /// <summary>Asks, for a run of this machine, the approval an environment requires.</summary>
    /// <param name="environmentId">The environment.</param>
    /// <param name="request">The run, its nonce and the version of the definition this machine verified.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval asked.</returns>
    /// <exception cref="ArgumentException"><paramref name="environmentId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetApprovalDto> RequestApprovalAsync(string environmentId, RequestFleetApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(environmentId);
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, $"api/v1/environments/{Uri.EscapeDataString(environmentId)}/approvals",
            Body(request, FleetSecretsApiJsonContext.Default.RequestFleetApprovalRequest), FleetSecretsApiJsonContext.Default.FleetApprovalDto, cancellationToken);
    }

    /// <summary>Reads an approval: its machine, or a person who may approve it.</summary>
    /// <param name="approvalId">The approval.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval.</returns>
    /// <exception cref="ArgumentException"><paramref name="approvalId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetApprovalDto> GetApprovalAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        return SendAsync(HttpMethod.Get, "api/v1/approvals/" + Uri.EscapeDataString(approvalId), null, FleetSecretsApiJsonContext.Default.FleetApprovalDto, cancellationToken);
    }

    /// <summary>Lists the approvals that wait for this device's person, and those of the machines of that person.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approvals, the newest first.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetApprovalListDto> ListApprovalsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/approvals", null, FleetSecretsApiJsonContext.Default.FleetApprovalListDto, cancellationToken);

    /// <summary>Gives the approval this device signed (<see cref="SignApproval"/>).</summary>
    /// <param name="approvalId">The approval.</param>
    /// <param name="signed">The signed approval.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval as the server holds it now.</returns>
    /// <exception cref="ArgumentException"><paramref name="approvalId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetApprovalDto> ApproveAsync(string approvalId, FleetSignedApproval signed, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(signed);
        return SendAsync(HttpMethod.Post, $"api/v1/approvals/{Uri.EscapeDataString(approvalId)}/approve",
            Body(new ApproveFleetApprovalRequest(signed), FleetSecretsApiJsonContext.Default.ApproveFleetApprovalRequest), FleetSecretsApiJsonContext.Default.FleetApprovalDto,
            cancellationToken);
    }

    /// <summary>Signs an environment with the key of a controller device, which it names as its signer.</summary>
    /// <param name="identity">The key of the device.</param>
    /// <param name="definition">The definition; its signer is set to this device.</param>
    /// <returns>The signed definition.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The definition does not hold.</exception>
    public static FleetSignedEnvironment SignEnvironment(DeviceIdentity identity, FleetEnvironmentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(definition);
        var text = FleetEnvironmentDocuments.Serialize(definition with { SignerDeviceId = identity.DeviceId });
        if (!FleetEnvironmentDocuments.TryRead(text, out _, out var reason)) throw new ArgumentException($"The environment does not hold: {reason}.", nameof(definition));
        return new FleetSignedEnvironment(text, Base64Url.EncodeToString(identity.SignData(FleetEnvironmentDocuments.GetSignedBytes(text))));
    }

    /// <summary>Signs the approval of a run with the key of a controller device of the approver, which it names as its signer.</summary>
    /// <param name="identity">The key of the device.</param>
    /// <param name="approval">The approval; its signer is set to this device.</param>
    /// <returns>The signed approval.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The approval does not hold.</exception>
    public static FleetSignedApproval SignApproval(DeviceIdentity identity, FleetEnvironmentApproval approval)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(approval);
        var text = FleetEnvironmentDocuments.Serialize(approval with { SignerDeviceId = identity.DeviceId });
        if (!FleetEnvironmentDocuments.TryReadApproval(text, out _, out var reason)) throw new ArgumentException($"The approval does not hold: {reason}.", nameof(approval));
        return new FleetSignedApproval(text, Base64Url.EncodeToString(identity.SignData(FleetEnvironmentDocuments.GetApprovalSignedBytes(text))));
    }
}
