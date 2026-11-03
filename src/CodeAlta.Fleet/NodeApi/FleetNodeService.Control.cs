using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.NodeApi;

public sealed partial class FleetNodeService
{
    /// <summary>The close code of the channels of a device whose permissions were reduced: it reconnects to go on.</summary>
    internal const string PermissionsChangedCloseCode = "permissions_changed";

    /// <summary>Changes what a device may do; its open channels close when it loses a permission.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="permissions">What it may do from now on.</param>
    /// <returns>The device as it is now and how many channels closed, or null when it is unknown or revoked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceId"/> is null.</exception>
    public async Task<(TrustedDevice Device, int ClosedChannels)?> SetPermissionsAsync(string deviceId, DevicePermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var before = _trusted.List().FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
        if (before is null || _trusted.SetPermissions(deviceId, permissions) is not { } after) return null;
        var closed = 0;
        // A watch opened under a permission the device lost would go on: its channels close, and it reconnects.
        if ((before.Permissions & ~after.Permissions) != DevicePermissions.None)
            closed = await CloseChannelsAsync(deviceId, PermissionsChangedCloseCode).ConfigureAwait(false);
        return (after, closed);
    }

    private int CountChannels(string deviceId)
        => _callers.Values.Count(caller => !caller.IsOwner && string.Equals(caller.DeviceId, deviceId, StringComparison.Ordinal));

    private async Task<int> CloseChannelsAsync(string deviceId, string code)
    {
        var closed = 0;
        foreach (var caller in _callers.Values.Where(caller => !caller.IsOwner && string.Equals(caller.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            try
            {
                await caller.Connection.CloseAsync(code).ConfigureAwait(false);
                closed++;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        return closed;
    }

    private FleetDeviceInfo DescribeDevice(TrustedDevice device)
        => new(device.DeviceId, device.Name, device.Fingerprint.ToDisplayString(), DevicePermissionNames.Format(device.Permissions), device.PairedAt,
            device.RevokedAt, CountChannels(device.DeviceId));

    // The device a person names on the command line: exactly one, or an error that says why not.
    private TrustedDevice ResolveDevice(string? text, bool includeRevoked)
    {
        var matches = _trusted.Match(text ?? "", includeRevoked);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FleetException(FleetNodeErrorCodes.DeviceNotFound, null, $"No paired device is named '{text}'."),
            _ => throw new FleetException(FleetErrorCodes.InvalidParams, null,
                $"'{text}' names several devices ({string.Join(", ", matches.Select(static device => device.DeviceId))}): give the device id."),
        };
    }

    // The answer goes first: stopping closes the channels, this one included.
    private Task<FleetEmpty> ControlStopAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var stop = _options.RequestStop ?? throw new FleetException(FleetNodeErrorCodes.Unavailable, null, "This node cannot be stopped from its local control.");
        LogInfo("The owner asked the node to stop.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None).ConfigureAwait(false);
            try
            {
                stop();
            }
            catch (ObjectDisposedException)
            {
                // The node stopped meanwhile.
            }
        }, CancellationToken.None);
        return Task.FromResult(new FleetEmpty());
    }

    private Task<FleetNodeStatus> ControlStatusAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
    {
        var devices = _trusted.List(includeRevoked: true);
        var revoked = devices.Count(static device => device.IsRevoked);
        return Task.FromResult(new FleetNodeStatus(_options.Name, _options.Identity.DeviceId, _options.Version, _options.AcceptsWork(),
            _host.RuntimeService.CountSessionsAtWork(), _callers.Values.Count(static caller => !caller.IsOwner), devices.Count - revoked, revoked)
        {
            Updating = _options.Updating(),
            Update = _options.UpdateStatus?.Invoke(),
        });
    }

    private Task<FleetDeviceList> ControlListDevicesAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => Task.FromResult(new FleetDeviceList([.. _trusted.List(includeRevoked: true).Select(DescribeDevice)]));

    private async Task<FleetDeviceChange> ControlRevokeDeviceAsync(FleetNodeCall call, FleetDeviceRef parameters, CancellationToken cancellationToken)
    {
        var device = ResolveDevice(parameters.Device, includeRevoked: true);
        var changed = _trusted.Revoke(device.DeviceId);
        // An already revoked device has no channel left to close, but a channel opened just before is closed all the same.
        var closed = await CloseChannelsAsync(device.DeviceId, FleetErrorCodes.Revoked).ConfigureAwait(false);
        if (changed) LogInfo($"Device {device.Name} ({device.DeviceId}) was revoked by the owner; {closed} channel(s) closed.");
        var now = _trusted.List(includeRevoked: true).First(other => string.Equals(other.DeviceId, device.DeviceId, StringComparison.Ordinal));
        return new FleetDeviceChange(DescribeDevice(now), changed, closed);
    }

    private Task<FleetProjectList> ControlListProjectsAsync(FleetNodeCall call, FleetEmpty parameters, CancellationToken cancellationToken)
        => _projectEditor.ListAsync(cancellationToken);

    private async Task<FleetProjectChange> ControlAddProjectAsync(FleetNodeCall call, FleetProjectAddParams parameters, CancellationToken cancellationToken)
    {
        var change = await _projectEditor.AddAsync(parameters.Path, parameters.Space, cancellationToken).ConfigureAwait(false);
        if (change.Changed) LogInfo($"The owner added the project {change.Project.Name} ({change.Project.Path}).");
        return change;
    }

    private async Task<FleetProjectChange> ControlCloneProjectAsync(FleetNodeCall call, FleetProjectCloneParams parameters, CancellationToken cancellationToken)
    {
        var change = await _projectEditor.CloneAsync(parameters.Url, parameters.Space, cancellationToken).ConfigureAwait(false);
        LogInfo($"The owner added the project {change.Project.Name} from {parameters.Url} ({change.Project.Path}).");
        return change;
    }

    private async Task<FleetProjectChange> ControlRemoveProjectAsync(FleetNodeCall call, FleetProjectRef parameters, CancellationToken cancellationToken)
    {
        var change = await _projectEditor.RemoveAsync(parameters.Project, CountProjectSessionsAsync, cancellationToken).ConfigureAwait(false);
        LogInfo($"The owner removed the project {change.Project.Name} ({change.Project.Path}) from the catalog.");
        return change;
    }

    // The sessions of a project as sessions.list finds them, archived ones included; those moved away run elsewhere.
    private async Task<int> CountProjectSessionsAsync(CodeAlta.Catalog.ProjectDescriptor project, CancellationToken cancellationToken)
    {
        var snapshot = await _host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var movedAway = _host.RuntimeService.Relinquishments.ListRelinquished();
        return snapshot.Sessions.Count(session => !movedAway.Contains(session.SessionId)
            && string.Equals(Scope(session, snapshot).Project?.Id, project.Id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<FleetDeviceChange> ControlSetPermissionsAsync(FleetNodeCall call, FleetDevicePermissionsParams parameters, CancellationToken cancellationToken)
    {
        if (!DevicePermissionNames.TryParse(parameters.Allow ?? [], out var allow, out var unknown)
            || !DevicePermissionNames.TryParse(parameters.Deny ?? [], out var deny, out unknown))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"'{unknown}' is not a permission: {string.Join(", ", DevicePermissionNames.All)}.");
        var device = ResolveDevice(parameters.Device, includeRevoked: true);
        if (device.IsRevoked)
            throw new FleetException(FleetErrorCodes.InvalidParams, null, $"{device.Name} ({device.DeviceId}) is revoked: pair it again to trust it.");
        var permissions = (device.Permissions | allow) & ~deny;
        var changed = await SetPermissionsAsync(device.DeviceId, permissions).ConfigureAwait(false)
            ?? throw new FleetException(FleetNodeErrorCodes.DeviceNotFound, null, $"{device.Name} ({device.DeviceId}) is no longer paired.");
        if (changed.Device.Permissions != device.Permissions)
            LogInfo($"The permissions of device {device.Name} ({device.DeviceId}) are now: {string.Join(", ", DevicePermissionNames.Format(permissions))}.");
        return new FleetDeviceChange(DescribeDevice(changed.Device), changed.Device.Permissions != device.Permissions, changed.ClosedChannels);
    }
}
