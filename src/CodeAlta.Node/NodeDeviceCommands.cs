using System.Globalization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode devices</c>, <c>revoke</c> and <c>permissions</c>.</summary>
internal sealed class NodeDeviceOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the permissions to add, as typed.</summary>
    public List<string> Allow { get; } = [];

    /// <summary>Gets the permissions to remove, as typed.</summary>
    public List<string> Deny { get; } = [];

    /// <summary>Gets the words: the device.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The devices paired with the node of a profile, managed by its owner: <c>altanode devices</c>, <c>revoke</c> and
/// <c>permissions</c>. While a node that listens runs on the profile, they go through its local control (a revoked device
/// is disconnected at once, a device that loses a permission reconnects); otherwise they read and change
/// <c>fleet/trusted-devices.json</c>, which the next node reads when it starts.
/// </summary>
internal sealed class NodeDeviceCommands(TextWriter output, TextWriter error)
{
    /// <summary>Gets or sets what stops a call to the node, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    public async ValueTask<int> DevicesAsync(NodeDeviceOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words.Count > 0) return await UsageAsync("devices takes no argument.").ConfigureAwait(false);
        var control = NodeLocalControl.Read(profile);
        IReadOnlyList<FleetDeviceInfo> devices;
        if (control.NodeListens)
        {
            var (client, identity) = await control.ConnectAsync(Stop).ConfigureAwait(false);
            using (identity)
            await using (client.ConfigureAwait(false))
            {
                devices = (await client.ListDevicesAsync(Stop).ConfigureAwait(false)).Devices;
            }
        }
        else
        {
            devices = [.. new TrustedDeviceStore(control.Catalog).List(includeRevoked: true).Select(static device => Describe(device, 0))];
        }

        if (devices.Count == 0)
        {
            await output.WriteLineAsync("No device is paired with this node. altanode pair makes a pairing link.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var nameWidth = Math.Min(32, devices.Max(static device => device.Name.Length));
        await output.WriteLineAsync($"{"NAME".PadRight(nameWidth)}  {"DEVICE ID",-26}  {"KEY",-9}  {"MAY",-22}  {"PAIRED",-16}  STATE").ConfigureAwait(false);
        foreach (var device in devices)
        {
            var state = device.RevokedAt is { } revoked
                ? $"revoked {Format(revoked)}"
                : control.NodeListens ? device.Channels > 0 ? $"connected ({device.Channels})" : "trusted" : "trusted";
            await output.WriteLineAsync($"{Fit(device.Name, nameWidth)}  {device.DeviceId,-26}  {ShortKey(device.Fingerprint),-9}  "
                + $"{(device.Permissions.Count == 0 ? "-" : string.Join(",", device.Permissions)),-22}  {Format(device.PairedAt),-16}  {state}").ConfigureAwait(false);
        }

        if (!control.NodeListens) await output.WriteLineAsync($"(read from {Store(control).FilePath}: no node that listens runs on this profile)").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    public async ValueTask<int> RevokeAsync(NodeDeviceOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words is not [var name]) return await UsageAsync("revoke takes the device: its name, its device id or the start of it (altanode devices lists them).").ConfigureAwait(false);
        var control = NodeLocalControl.Read(profile);
        if (control.NodeListens)
        {
            var change = await CallAsync(control, (client, token) => client.RevokeDeviceAsync(name, token)).ConfigureAwait(false);
            if (change is null) return NodeExitCodes.Failure;
            await output.WriteLineAsync(change.Changed
                ? $"Revoked {change.Device.Name} ({change.Device.DeviceId}): its key is refused from now on{(change.ClosedChannels > 0 ? $"; {change.ClosedChannels} open channel(s) closed" : "")}."
                : $"{change.Device.Name} ({change.Device.DeviceId}) was already revoked.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var store = Store(control);
        if (await ResolveAsync(store, name, "revoke") is not { } device) return NodeExitCodes.Failure;
        var revoked = store.Revoke(device.DeviceId);
        await output.WriteLineAsync(revoked
            ? $"Revoked {device.Name} ({device.DeviceId}) in {store.FilePath}: the node refuses its key when it starts."
            : $"{device.Name} ({device.DeviceId}) was already revoked.").ConfigureAwait(false);
        return await WarnIfNodeStartedAsync(control).ConfigureAwait(false);
    }

    public async ValueTask<int> PermissionsAsync(NodeDeviceOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words is not [var name]) return await UsageAsync("permissions takes the device: its name, its device id or the start of it (altanode devices lists them).").ConfigureAwait(false);
        if (!DevicePermissionNames.TryParse(options.Allow, out var allow, out var unknown) || !DevicePermissionNames.TryParse(options.Deny, out var deny, out unknown))
            return await UsageAsync($"'{unknown}' is not a permission: {string.Join(", ", DevicePermissionNames.All)}.").ConfigureAwait(false);
        var change = options.Allow.Count > 0 || options.Deny.Count > 0;
        var control = NodeLocalControl.Read(profile);
        if (control.NodeListens)
        {
            FleetDeviceInfo device;
            var closed = 0;
            var changed = false;
            if (change)
            {
                var result = await CallAsync(control, (client, token) => client.SetDevicePermissionsAsync(
                    new FleetDevicePermissionsParams(name, DevicePermissionNames.Format(allow), DevicePermissionNames.Format(deny)), token)).ConfigureAwait(false);
                if (result is null) return NodeExitCodes.Failure;
                (device, closed, changed) = (result.Device, result.ClosedChannels, result.Changed);
            }
            else
            {
                var list = await CallAsync(control, (client, token) => client.ListDevicesAsync(token)).ConfigureAwait(false);
                if (list is null) return NodeExitCodes.Failure;
                var matches = Match(list.Devices, name);
                if (matches.Count != 1)
                {
                    await error.WriteLineAsync(NotOne("permissions", name, matches.Select(static device => device.DeviceId))).ConfigureAwait(false);
                    return NodeExitCodes.Failure;
                }

                device = matches[0];
            }

            await WritePermissionsAsync(device.Name, device.DeviceId, device.Permissions, change, changed).ConfigureAwait(false);
            if (closed > 0) await output.WriteLineAsync($"It lost a permission: {closed} open channel(s) closed; it reconnects with what it may do now.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var store = Store(control);
        if (await ResolveAsync(store, name, "permissions") is not { } trusted) return NodeExitCodes.Failure;
        if (!change)
        {
            await WritePermissionsAsync(trusted.Name, trusted.DeviceId, DevicePermissionNames.Format(trusted.Permissions), false, false).ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        if (trusted.IsRevoked)
        {
            await error.WriteLineAsync($"altanode permissions: {trusted.Name} ({trusted.DeviceId}) is revoked: pair it again to trust it.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var updated = store.SetPermissions(trusted.DeviceId, (trusted.Permissions | allow) & ~deny)!;
        await WritePermissionsAsync(updated.Name, updated.DeviceId, DevicePermissionNames.Format(updated.Permissions), true, updated.Permissions != trusted.Permissions)
            .ConfigureAwait(false);
        return await WarnIfNodeStartedAsync(control).ConfigureAwait(false);
    }

    /// <summary>Describes a trusted device as the local control of a node does.</summary>
    internal static FleetDeviceInfo Describe(TrustedDevice device, int channels)
        => new(device.DeviceId, device.Name, device.Fingerprint.ToDisplayString(), DevicePermissionNames.Format(device.Permissions), device.PairedAt, device.RevokedAt, channels);

    // The first two groups of the fingerprint as people read it: enough to tell keys apart in a list.
    private static string ShortKey(string fingerprint)
    {
        var groups = fingerprint.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return groups.Length >= 2 ? groups[0] + " " + groups[1] : fingerprint;
    }

    private async Task WritePermissionsAsync(string name, string deviceId, IReadOnlyList<string> permissions, bool asked, bool changed)
    {
        var may = permissions.Count == 0 ? "nothing (it stays known)" : string.Join(", ", permissions);
        await output.WriteLineAsync(asked && !changed
            ? $"{name} ({deviceId}) may already {may}: nothing changed."
            : $"{name} ({deviceId}) may {may}.").ConfigureAwait(false);
    }

    private async Task<T?> CallAsync<T>(NodeLocalControl control, Func<Fleet.Client.FleetNodeClient, CancellationToken, Task<T>> call) where T : class
    {
        var (client, identity) = await control.ConnectAsync(Stop).ConfigureAwait(false);
        using (identity)
        await using (client.ConfigureAwait(false))
        {
            try
            {
                return await call(client, Stop).ConfigureAwait(false);
            }
            catch (FleetException exception) when (exception.Error.Code is FleetNodeErrorCodes.DeviceNotFound or FleetErrorCodes.InvalidParams)
            {
                await error.WriteLineAsync("altanode: " + exception.Error.Message).ConfigureAwait(false);
                return null;
            }
        }
    }

    private async Task<TrustedDevice?> ResolveAsync(TrustedDeviceStore store, string name, string command)
    {
        var matches = store.Match(name, includeRevoked: true);
        if (matches.Count == 1) return matches[0];
        await error.WriteLineAsync(NotOne(command, name, matches.Select(static device => device.DeviceId))).ConfigureAwait(false);
        return null;
    }

    private static IReadOnlyList<FleetDeviceInfo> Match(IReadOnlyList<FleetDeviceInfo> devices, string text)
    {
        var wanted = text.Trim();
        var exact = devices.Where(device => string.Equals(device.DeviceId, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0 || wanted.Length < 4) return exact;
        return devices.Where(device => device.DeviceId.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string NotOne(string command, string name, IEnumerable<string> deviceIds)
    {
        var ids = deviceIds.ToArray();
        return ids.Length == 0
            ? $"altanode {command}: no paired device is named '{name}' (altanode devices lists them)."
            : $"altanode {command}: '{name}' names several devices ({string.Join(", ", ids)}): give the device id.";
    }

    // A node that started while the file was changed read it before: it keeps what it read until it restarts.
    private async Task<int> WarnIfNodeStartedAsync(NodeLocalControl before)
    {
        if (!NodeLocalControl.Read(before.Profile).NodeListens) return NodeExitCodes.Success;
        await error.WriteLineAsync("altanode: a node started on this profile meanwhile and may not have seen the change: run the command again.").ConfigureAwait(false);
        return NodeExitCodes.Failure;
    }

    private static TrustedDeviceStore Store(NodeLocalControl control) => new(control.Catalog);

    private static string Format(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Fit(string text, int width) => text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
