using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The labels of a machine this computer manages (<c>doc/fleet/node.md</c>), on its page, as <c>altanode labels</c> sets them
/// there: the machine publishes them to its servers, and the environments of the fleet run on the machines with their labels.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Reads the labels of a machine.</summary>
    [NeoRpcMethod("labels")]
    public async Task<MachineLabelsView> LabelsAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, []);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return new("invalid_request", null, []);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, []);
        try
        {
            var labels = await host.ManageAsync((client, token) => client.GetLabelsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", null, [.. labels.Labels]);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, []); }
        catch (FleetException exception) { return new(Code(exception), exception.Error.Message, []); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, []); }
    }

    /// <summary>Sets the labels of a machine, which it publishes at its next publication.</summary>
    [NeoRpcMethod("setLabels")]
    public Task<MachineManageResult> SetLabelsAsync(MachineLabelsRequest request, CancellationToken cancellationToken)
    {
        if (request?.Labels is not { Length: <= FleetMachineLabels.MaxLabels } labels || labels.Any(label => !FleetMachineLabels.IsLabel(label?.Trim())))
            return Invalid(request?.ExpectedEpoch, $"A label is letters, digits, -, _, . and :, at most {FleetMachineLabels.MaxLength}; a machine has at most {FleetMachineLabels.MaxLabels}.");
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.SetLabelsAsync([.. labels.Select(static label => label.Trim())], token),
            cancellationToken);
    }
}

/// <summary>The labels of a machine, as the person who manages it sees them.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Labels">The labels.</param>
internal sealed record MachineLabelsView(string Status, string? Message, string[] Labels);

/// <summary>The labels to give a machine, replacing those it has.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Labels">The labels.</param>
internal sealed record MachineLabelsRequest(string ExpectedEpoch, string MachineId, string[] Labels);
