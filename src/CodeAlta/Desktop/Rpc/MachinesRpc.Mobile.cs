using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page of the phones of a machine this computer manages by pairing (<c>control.mobile.*</c>, <c>doc/fleet/node.md</c>): whether
/// the machine serves it, turning it on or off, and the calls of the Mobile tab that draw its QR code. The machine runs its own
/// Mobile plugin and turns it on itself, since a plugin that is off hears no request; this computer only forwards. A pairing code
/// the machine makes is good for one phone and a few minutes, and never leaves this window.
/// </summary>
internal sealed partial class MachinesService
{
    // The calls of the Mobile tab the page of a machine makes: its status, a code for one phone, asking the network again, and an action
    // of the network (a sign-in), plus its settings and Reset access. Only a paired manager reaches this control.
    private static readonly System.Collections.Immutable.ImmutableArray<string> MobileCalls =
        ["mobile.status", "mobile.pair", "mobile.retry", "mobile.act", "mobile.settings.get", "mobile.settings.set", "mobile.reset"];

    /// <summary>Reads whether a machine serves the page of the phones.</summary>
    [NeoRpcMethod("mobile")]
    public Task<MachineMobileView> MobileAsync(MachineRequest request, CancellationToken cancellationToken)
        => MobileStateAsync(request?.ExpectedEpoch, request?.MachineId, (client, token) => client.GetMobileAsync(token), changes: false, cancellationToken);

    /// <summary>Turns the page of the phones of a machine on or off: the machine writes the setting and applies it itself.</summary>
    [NeoRpcMethod("mobileEnable")]
    public Task<MachineMobileView> MobileEnableAsync(MachineMobileEnableRequest request, CancellationToken cancellationToken)
        => MobileStateAsync(request?.ExpectedEpoch, request?.MachineId, (client, token) => client.EnableMobileAsync(request!.Enabled, token), changes: true, cancellationToken);

    /// <summary>Runs one call of the Mobile tab on a machine, and gives the JSON the tab reads.</summary>
    [NeoRpcMethod("mobileInvoke")]
    public async Task<MachineMobileInvokeView> MobileInvokeAsync(MachineMobileInvokeRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.Method is not { } method || !MobileCalls.Contains(method, StringComparer.Ordinal)
            || request.Payload is { Length: > FleetMobileInvokeParams.MaxPayload })
            return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var json = await host.ManageAsync((client, token) => client.InvokeMobileAsync(method, request.Payload, token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", null, json);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(MobileCode(exception), exception.Error.Message, null); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, null); }
    }

    private async Task<MachineMobileView> MobileStateAsync(string? expectedEpoch, string? machineId, Func<FleetNodeClient, CancellationToken, Task<FleetMobileState>> call,
        bool changes, CancellationToken cancellationToken)
    {
        if (Refuse(expectedEpoch) is { } refused) return MachineMobileView.Refused(refused);
        if (machineId is not { Length: > 0 and <= 64 } id) return MachineMobileView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineMobileView.Refused("not_found");
        try
        {
            var state = await host.ManageAsync(call, changes, cancellationToken).ConfigureAwait(false);
            return new("ok", null, state.Available, state.Enabled, state.Running, state.Refusal);
        }
        catch (ObjectDisposedException) { return MachineMobileView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineMobileView.Refused(MobileCode(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineMobileView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    // A machine of an older version does not know the calls: the page says to update it, rather than that it could not.
    private static string MobileCode(FleetException exception)
        => exception.Error.Code == FleetErrorCodes.MethodNotFound ? "mobile.unsupported" : Code(exception);
}

/// <summary>Whether a machine serves the page of the phones, as the page of the machine shows it.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>mobile.unsupported</c> (a machine of an older version), <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Available">Whether the machine can serve it: it has the plugin and the files of the page.</param>
/// <param name="Enabled">Whether it is turned on.</param>
/// <param name="Running">Whether the plugin runs now.</param>
/// <param name="Refusal">Why the machine does not serve it, for people; null when nothing keeps it from it.</param>
internal sealed record MachineMobileView(string Status, string? Message, bool Available, bool Enabled, bool Running, string? Refusal)
{
    internal static MachineMobileView Refused(string status) => new(status, null, false, false, false, null);
}

/// <summary>Turning the page of the phones of a machine on or off.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Enabled">Whether the page is turned on.</param>
internal sealed record MachineMobileEnableRequest(string ExpectedEpoch, string MachineId, bool Enabled);

/// <summary>One call of the Mobile tab, for a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Method">One of the Mobile tab or settings calls declared by the node's mobile control.</param>
/// <param name="Payload">The JSON of its parameters; null for a call that takes none.</param>
internal sealed record MachineMobileInvokeRequest(string ExpectedEpoch, string MachineId, string Method, string? Payload);

/// <summary>The answer of a call of the Mobile tab.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c> (the page is off on the machine), <c>policy.refused</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Json">The JSON of the answer, as the tab reads it.</param>
internal sealed record MachineMobileInvokeView(string Status, string? Message, string? Json);
