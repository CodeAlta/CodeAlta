using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// What a controller device of a person does with the secrets, variables and environments of that person at one server
/// (<c>doc/fleet/secrets.md</c>): it seals each value for the machines allowed to use it and for the person's controller
/// devices, from the device list as the anchor of the link verified it (<see cref="FleetOrganizationDevices"/>), never from
/// the server's tables; it rotates, deletes, gives a machine allowed later its copy, signs environments and approves runs.
/// </summary>
/// <remarks>Values are write-only: a secret is never read back; a variable is, from the copy sealed for this device.</remarks>
public sealed class FleetSecretsManager
{
    /// <summary>How long an approval this device gives stays valid.</summary>
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromHours(1);

    private readonly FleetOrganizationDevices _devices;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FleetSecretsManager"/> class.</summary>
    /// <param name="devices">The anchored device list of the server, whose key and HTTP handler the calls use too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public FleetSecretsManager(FleetOrganizationDevices devices) : this(devices, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FleetSecretsManager"/> class with a clock.</summary>
    /// <param name="devices">The anchored device list of the server, whose key and HTTP handler the calls use too.</param>
    /// <param name="time">The clock of the approvals.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetSecretsManager(FleetOrganizationDevices devices, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(time);
        _devices = devices;
        _time = time;
    }

    /// <summary>Gets the anchored device list the values are sealed by.</summary>
    public FleetOrganizationDevices Devices => _devices;

    /// <summary>Lists the values of this device's person.</summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The values.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretListDto> ListAsync(CancellationToken cancellationToken)
    {
        using var client = Client();
        return await client.ListSecretsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a value for the machines allowed to use it: a new one at version 1, or the next version of the value of this
    /// kind, name and scope (a rotation), sealed again for the machines given.
    /// </summary>
    /// <param name="kind">The kind of the value (<see cref="FleetSecretKinds"/>).</param>
    /// <param name="name">Its name.</param>
    /// <param name="scope">Where it applies.</param>
    /// <param name="value">The value.</param>
    /// <param name="hostOnly">Whether it goes only to host-side tools, never to the agent's shell.</param>
    /// <param name="machines">The machines allowed to use it (device ids of the person's machines).</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The value does not hold, or this device is not a controller device of the list.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify: nothing is sealed.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretDto> SetAsync(string kind, string name, FleetSecretScopeDto scope, string value, bool hostOnly, IReadOnlyCollection<string> machines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(machines);
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        using var client = Client();
        var held = (await client.ListSecretsAsync(cancellationToken).ConfigureAwait(false)).Secrets.FirstOrDefault(secret => secret.Kind == kind
            && string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase) && secret.Scope.Kind == scope.Kind && secret.Scope.Id == scope.Id);
        var binding = held is null
            ? new FleetSecretBinding(FleetSecretSealing.NewId(), kind, name, scope.Kind, scope.Id, 1, hostOnly)
            : FleetSecretBinding.Of(held) with { Version = held.Version + 1, HostOnly = hostOnly, Name = name };
        var copies = FleetSecretSealing.Seal(value, binding, FleetSecretSealing.Recipients(list, person, machines), list, _devices.Identity);
        return await client.PutSecretAsync(binding.SecretId, new SetFleetSecretRequest(kind, binding.Name, scope, binding.Version, hostOnly, copies), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Rotates a value: its next version, sealed for the machines that hold it now by a grant a controller device of the
    /// person signed and are still in the list (a revoked machine is not given the new one, nor a holder the server lists
    /// by itself) and for the person's controller devices; the machines take it at their next run.
    /// </summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentException">The id is empty or the value does not hold.</exception>
    /// <exception cref="KeyNotFoundException">No value of the person has this id.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify: nothing is sealed.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretDto> RotateAsync(string id, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(value);
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        using var client = Client();
        var held = await FindAsync(client, id, cancellationToken).ConfigureAwait(false);
        var binding = FleetSecretBinding.Of(held) with { Version = held.Version + 1 };
        var granted = FleetSecretSealing.GrantedHolders(held, list).Intersect(held.Holders, StringComparer.Ordinal);
        var copies = FleetSecretSealing.Seal(value, binding, FleetSecretSealing.Recipients(list, person, granted), list, _devices.Identity);
        return await client.PutSecretAsync(id, new SetFleetSecretRequest(held.Kind, held.Name, held.Scope, binding.Version, held.HostOnly, copies), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Deletes a value and every copy of it.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values left.</returns>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretListDto> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var client = Client();
        return await client.DeleteSecretAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a machine allowed later its copy of the current version ("give <c>GRAFANA_TOKEN</c> to <c>build-mac</c>"): this
    /// device opens its own copy and wraps the key to that machine, as it approves a new device.
    /// </summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="machineId">The machine, a device of the person in the list.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentException">An id is empty, or the machine is not a device of the person in the list.</exception>
    /// <exception cref="KeyNotFoundException">No value of the person has this id.</exception>
    /// <exception cref="InvalidDataException">The copy of this device does not open.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretDto> GrantAsync(string id, string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        if (FleetSecretSealing.Recipients(list, person, [machineId]).FirstOrDefault(member => member.DeviceId == machineId) is not { } machine)
            throw new ArgumentException("The machine is not a device of this person in the device list.", nameof(machineId));
        using var client = Client();
        var held = await FindAsync(client, id, cancellationToken).ConfigureAwait(false);
        var own = held.Copy ?? throw new InvalidDataException("This device holds no copy of the value to give.");
        var copy = FleetSecretSealing.Regrant(own, FleetSecretBinding.Of(held), machine, _devices.Identity, list);
        return await client.AddSecretCopiesAsync(id, new AddFleetSecretCopiesRequest(held.Version, [copy]), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes a machine's copy of a value away: it does not get the value at its next run.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value as the server holds it now.</returns>
    /// <exception cref="ArgumentException">An id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretDto> WithdrawAsync(string id, string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        using var client = Client();
        return await client.RemoveSecretCopyAsync(id, machineId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a variable back from the copy sealed for this device; a secret is never read back.</summary>
    /// <param name="variable">The variable, as listed.</param>
    /// <returns>Its value, or null when the copy of this device does not open (or it is a secret).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is null.</exception>
    public string? ReadVariable(FleetSecretDto variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (variable.Kind != FleetSecretKinds.Variable || _devices.Current is not { } list) return null;
        return FleetSecretSealing.TryOpen(variable.Copy, FleetSecretBinding.Of(variable), _devices.Identity, list, out var value, out _) ? value : null;
    }

    /// <summary>Lists the environments of this device's person, each with its definition when it verifies against the device list.</summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The environments and their definitions (null for one that does not verify).</returns>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<(FleetEnvironmentDto Environment, FleetEnvironmentDefinition? Definition)>> ListEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var client = Client();
        var environments = await client.ListEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
        return [.. environments.Environments.Select(environment => (environment,
            FleetEnvironmentDocuments.TryVerify(environment.Signed, list, _devices.Identity.DeviceId, out var definition, out _) ? definition : null))];
    }

    /// <summary>Signs and stores an environment of this device's person: a new one, or the next version of the one of that name.</summary>
    /// <param name="name">The name of the environment.</param>
    /// <param name="approvers">The people (ids) one of whom approves each run that uses it; empty for none.</param>
    /// <param name="machineLabels">The labels a machine must have, one of them; empty for every machine of the person.</param>
    /// <param name="automations">The automations that may use it; empty for every run.</param>
    /// <param name="removed">Whether it is taken away.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The environment as the server holds it.</returns>
    /// <exception cref="ArgumentException">The name or a list does not hold, or this device is not a controller device of the list.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetEnvironmentDto> PutEnvironmentAsync(string name, IReadOnlyList<string> approvers, IReadOnlyList<string> machineLabels, IReadOnlyList<string> automations,
        bool removed, CancellationToken cancellationToken)
    {
        if (!FleetEnvironmentDocuments.IsName(name)) throw new ArgumentException("The name of an environment is letters, digits, - and _, at most 64.", nameof(name));
        ArgumentNullException.ThrowIfNull(approvers);
        ArgumentNullException.ThrowIfNull(machineLabels);
        ArgumentNullException.ThrowIfNull(automations);
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        using var client = Client();
        var held = (await client.ListEnvironmentsAsync(cancellationToken).ConfigureAwait(false)).Environments.FirstOrDefault(environment => environment.Name == name);
        var definition = new FleetEnvironmentDefinition(FleetEnvironmentDefinition.CurrentVersion, list.OrganizationId ?? throw new ArgumentException("The list names no organization yet."),
            person, held?.Id ?? FleetSecretSealing.NewId(), name, (held?.Version ?? 0) + 1, [.. approvers.Distinct(StringComparer.Ordinal)],
            [.. machineLabels.Distinct(StringComparer.Ordinal)], [.. automations.Distinct(StringComparer.Ordinal)], removed, _devices.Identity.DeviceId, Now());
        return await client.PutEnvironmentAsync(FleetServerClient.SignEnvironment(_devices.Identity, definition), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the approvals that wait for this device's person, and those of the person's machines, the newest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approvals.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetApprovalListDto> ListApprovalsAsync(CancellationToken cancellationToken)
    {
        using var client = Client();
        return await client.ListApprovalsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives what a person reviews of an approval, as a short text that the approval they click must still match: its id,
    /// the environment, its name and version, the machine, the run, the nonce, the automation and the title.
    /// </summary>
    /// <param name="approval">The approval as it was shown.</param>
    /// <returns>The text, the same for the same approval.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="approval"/> is null.</exception>
    public static string ReviewOf(FleetApprovalDto approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var text = string.Join('\n', "codealta-approval-review/1", approval.Id, approval.EnvironmentId, approval.EnvironmentName,
            approval.EnvironmentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), approval.MachineId, approval.RunId, approval.Nonce,
            approval.AutomationId ?? string.Empty, approval.Title ?? string.Empty);
        return System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Approves the run a person reviewed, which waits for an environment: this device signs an approval bound to that
    /// environment and its version, the machine, the run and the nonce the machine chose, valid for <see cref="ApprovalLifetime"/>.
    /// The approval is read again first and must be the one reviewed (<see cref="ReviewOf"/>): a server that answers another
    /// run, machine or nonce for the same approval gets nothing signed.
    /// </summary>
    /// <param name="approvalId">The approval.</param>
    /// <param name="reviewed">What the person reviewed of it (<see cref="ReviewOf"/> of the approval shown).</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The approval as the server holds it now.</returns>
    /// <exception cref="ArgumentException">An argument is empty, or this device is not a controller device of the list.</exception>
    /// <exception cref="InvalidDataException">The approval the server answers is not the one reviewed, or not one its machine asked (<see cref="FleetApprovalRequests"/>): nothing is signed.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused it: this device's person is not an approver, or it was given already.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetApprovalDto> ApproveAsync(string approvalId, string reviewed, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentException.ThrowIfNullOrEmpty(reviewed);
        var list = await _devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var person = PersonOf(list);
        using var client = Client();
        var asked = await client.GetApprovalAsync(approvalId, cancellationToken).ConfigureAwait(false);
        if (asked.Id != approvalId || !string.Equals(ReviewOf(asked), reviewed, StringComparison.Ordinal))
            throw new InvalidDataException("The approval changed since it was shown: read the approvals again before approving.");
        // What the machine asked itself, never a request the server made up or changed.
        if (!FleetApprovalRequests.IsAskedBy(asked, list, out var notAsked)) throw new InvalidDataException($"The run is not one its machine asked: {notAsked}.");
        var now = Now();
        var approval = new FleetEnvironmentApproval(FleetEnvironmentApproval.CurrentVersion, list.OrganizationId!, asked.Id, asked.EnvironmentId, asked.EnvironmentVersion,
            asked.MachineId, asked.RunId, asked.Nonce, person, _devices.Identity.DeviceId, now, now + ApprovalLifetime);
        return await client.ApproveAsync(approvalId, FleetServerClient.SignApproval(_devices.Identity, approval), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the uses the machines published of a value.</summary>
    /// <param name="id">The id of the value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The uses, the newest first.</returns>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetSecretUseListDto> ListUsesAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        using var client = Client();
        return await client.ListSecretUsesAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private FleetServerClient Client() => _devices.CreateClient();

    // To the second, as the signed documents of the fleet are.
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeSeconds(_time.GetUtcNow().ToUnixTimeSeconds());

    private string PersonOf(FleetDeviceListState list)
        => list.Members.TryGetValue(_devices.Identity.DeviceId, out var self) && self.Kind == FleetDeviceKinds.Controller && self.OwnerId is { } person
            ? person
            : throw new ArgumentException("Only a controller device of the device list, with its person, manages its secrets.");

    private static async Task<FleetSecretDto> FindAsync(FleetServerClient client, string id, CancellationToken cancellationToken)
        => (await client.ListSecretsAsync(cancellationToken).ConfigureAwait(false)).Secrets.FirstOrDefault(secret => secret.Id == id)
            ?? throw new KeyNotFoundException("No value of this person has this id.");
}
