using System.Globalization;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Identity;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// This computer as a machine of the person's other computers (<see cref="DesktopNode"/>), in Settings > Fleet > Machines:
/// whether they may pair with it and reach it, a pairing link for one of them, and what each computer paired with it may do.
/// The link and the key stay in the host until the person asks for a link: the page shows names, addresses and words.
/// </summary>
internal sealed partial class MachinesService
{
    // What the person changes for a computer paired with this one: what a desktop serves (it runs no automation for others,
    // and no other computer manages it).
    private static readonly string[] ThisComputerPermissions = ["view", "drive", "start", "move"];

    /// <summary>Gets or sets this computer as a machine of the person's other computers; null where the desktop has none.</summary>
    internal DesktopNode? Node { get; set; }

    /// <summary>Whether the person's other computers may pair with this one, where they reach it, and the computers paired with it.</summary>
    [NeoRpcMethod("thisComputer")]
    public ThisComputerView ThisComputer(MachinesRequest request)
    {
        if (RefuseNode(request?.ExpectedEpoch) is { } refused) return ThisComputerView.Refused(refused);
        return View(Node!, "ok");
    }

    /// <summary>Lets the person's other computers pair with this one and reach it, or stops.</summary>
    [NeoRpcMethod("setVisible")]
    public async Task<ThisComputerView> SetVisibleAsync(ThisComputerVisibleRequest request, CancellationToken cancellationToken)
    {
        if (RefuseNode(request?.ExpectedEpoch) is { } refused) return ThisComputerView.Refused(refused);
        try
        {
            await Node!.SetVisibleAsync(request!.Visible, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return View(Node!, "write_failed");
        }
        catch (ObjectDisposedException)
        {
            return ThisComputerView.Refused("unavailable");
        }

        return View(Node, "ok");
    }

    /// <summary>Makes a pairing link for another computer of the person, which it pastes in Add a machine… there.</summary>
    [NeoRpcMethod("createPairingLink")]
    public ThisComputerPairingLink CreatePairingLink(MachinesRequest request)
    {
        if (RefuseNode(request?.ExpectedEpoch) is { } refused) return ThisComputerPairingLink.Refused(refused);
        try
        {
            var pairing = Node!.CreatePairing();
            return new("ok", pairing.Link, pairing.Words, pairing.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), [.. pairing.Addresses]);
        }
        catch (InvalidOperationException)
        {
            return ThisComputerPairingLink.Refused("not_listening");
        }
        catch (ArgumentException)
        {
            // No address of this computer can go in a link.
            return ThisComputerPairingLink.Refused("not_listening");
        }
    }

    /// <summary>Waits until the open pairing link is used, expires or is replaced, and says which, with the computer that paired.</summary>
    [NeoRpcMethod("waitPairing")]
    public async Task<ThisComputerPairingOutcome> WaitPairingAsync(MachinesRequest request, CancellationToken cancellationToken)
    {
        if (RefuseNode(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (Node!.CurrentPairing() is not { } outcome) return new("none", null);
        try
        {
            var result = await outcome.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == "redeemed") Changed();
            return new(result.Status, result.DeviceName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new("cancelled", null);
        }
    }

    /// <summary>Gives a computer paired with this one a permission, or takes it away.</summary>
    [NeoRpcMethod("setThisComputerDevicePermission")]
    public ThisComputerView SetThisComputerDevicePermission(ThisComputerDevicePermissionRequest request)
    {
        if (RefuseNode(request?.ExpectedEpoch) is { } refused) return ThisComputerView.Refused(refused);
        if (!ValidDevice(request!.DeviceId) || !ThisComputerPermissions.Contains(request.Permission, StringComparer.Ordinal)
            || !DevicePermissionNames.TryParse([request.Permission], out var permission, out _))
            return View(Node!, "invalid_request");
        try
        {
            return View(Node!, Node!.SetPermission(request.DeviceId, permission, request.Allowed) ? "ok" : "not_found");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return View(Node!, "write_failed");
        }
    }

    /// <summary>Revokes a computer paired with this one: its key is refused here from now on.</summary>
    [NeoRpcMethod("revokeThisComputerDevice")]
    public async Task<ThisComputerView> RevokeThisComputerDeviceAsync(ThisComputerDeviceRequest request)
    {
        if (RefuseNode(request?.ExpectedEpoch) is { } refused) return ThisComputerView.Refused(refused);
        if (!ValidDevice(request!.DeviceId)) return View(Node!, "invalid_request");
        try
        {
            return View(Node!, await Node!.RevokeAsync(request.DeviceId).ConfigureAwait(false) ? "ok" : "not_found");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return View(Node!, "write_failed");
        }
    }

    private string? RefuseNode(string? expectedEpoch) => Refuse(expectedEpoch) ?? (Node is null ? "unavailable" : null);

    private static ThisComputerView View(DesktopNode node, string status)
    {
        IReadOnlyList<TrustedDevice> devices;
        try
        {
            devices = node.Devices();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            devices = [];
        }

        return new ThisComputerView(status, node.Visible, node.Listening, node.Name, node.Words, node.Port, [.. node.Addresses], node.TailnetName, node.Problem,
            [.. devices.Select(static device => new MachineDeviceView(device.DeviceId, device.Name, device.Fingerprint.ToDisplayString(),
                [.. DevicePermissionNames.Format(device.Permissions).Where(static name => ThisComputerPermissions.Contains(name, StringComparer.Ordinal))],
                device.PairedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), null, 0, false))]);
    }
}

/// <summary>This computer as a machine of the person's other computers.</summary>
/// <param name="Status"><c>ok</c>, <c>write_failed</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Visible">Whether the person lets their other computers pair with this one and reach it.</param>
/// <param name="Listening">Whether it listens now.</param>
/// <param name="Name">The name the other computers list it under.</param>
/// <param name="Words">The six words of its key, once it has one.</param>
/// <param name="Port">The port it listens on, or would listen on.</param>
/// <param name="Addresses">Where the other computers reach it while it listens.</param>
/// <param name="Tailnet">Its name on its Tailscale network while it listens there, which reaches it away from home too.</param>
/// <param name="Problem">Why it does not listen though it is visible, in words.</param>
/// <param name="Devices">The computers paired with it, and what each may do here.</param>
internal sealed record ThisComputerView(string Status, bool Visible, bool Listening, string? Name, string? Words, int Port, string[] Addresses, string? Tailnet,
    string? Problem, MachineDeviceView[] Devices)
{
    internal static ThisComputerView Refused(string status) => new(status, false, false, null, null, 0, [], null, null, []);
}

/// <summary>Lets the person's other computers pair with this one, or stops.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Visible">Whether they may.</param>
internal sealed record ThisComputerVisibleRequest(string ExpectedEpoch, bool Visible);

/// <summary>A pairing link made here for another computer of the person.</summary>
/// <param name="Status"><c>ok</c>, <c>not_listening</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Link">The <c>codealta-pair://</c> link.</param>
/// <param name="Words">The six words of the key of this computer, which the other computer shows before it pairs.</param>
/// <param name="ExpiresAt">When the link expires.</param>
/// <param name="Addresses">Where the link says this computer listens.</param>
internal sealed record ThisComputerPairingLink(string Status, string? Link, string? Words, string? ExpiresAt, string[] Addresses)
{
    internal static ThisComputerPairingLink Refused(string status) => new(status, null, null, null, []);
}

/// <summary>What became of the open pairing link.</summary>
/// <param name="Status"><c>redeemed</c>, <c>expired</c>, <c>replaced</c>, <c>none</c> (no link is open), <c>cancelled</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="DeviceName">The computer that paired with it, once redeemed.</param>
internal sealed record ThisComputerPairingOutcome(string Status, string? DeviceName);

/// <summary>A permission of a computer paired with this one.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="Permission"><c>view</c>, <c>drive</c>, <c>start</c> or <c>move</c>.</param>
/// <param name="Allowed">Whether it has it.</param>
internal sealed record ThisComputerDevicePermissionRequest(string ExpectedEpoch, string DeviceId, string Permission, bool Allowed);

/// <summary>A computer paired with this one.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="DeviceId">The device.</param>
internal sealed record ThisComputerDeviceRequest(string ExpectedEpoch, string DeviceId);
