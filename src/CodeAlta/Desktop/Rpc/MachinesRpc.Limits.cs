using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The limits of a machine this computer manages (<c>doc/fleet/node.md</c>, limits), on its page, as <c>altanode limits</c> sets
/// them there: how many sessions work at once; and the queue of the runs that wait for a seat, which a person starts now or cancels.
/// </summary>
internal sealed partial class MachinesService
{
    /// <summary>Reads the limits of a machine.</summary>
    [NeoRpcMethod("limits")]
    public async Task<MachineLimitsView> LimitsAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var limits = await host.ManageAsync((client, token) => client.GetLimitsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", null, limits.Sessions);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(Code(exception), exception.Error.Message, null); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, null); }
    }

    /// <summary>Sets how many sessions work at once on a machine; null for no limit.</summary>
    [NeoRpcMethod("setLimits")]
    public Task<MachineManageResult> SetLimitsAsync(MachineLimitsRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Sessions is < 1 or > FleetMachineLimits.MaxSessions)
            return Invalid(request?.ExpectedEpoch, $"A machine runs between 1 and {FleetMachineLimits.MaxSessions} sessions at once, or has no limit.");
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.SetLimitsAsync(request.Sessions, token), cancellationToken);
    }

    /// <summary>Reads the seats of a machine: its limit, the seats taken and the runs that wait.</summary>
    [NeoRpcMethod("seats")]
    public async Task<MachineSeatsView> SeatsAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineSeatsView.Refused(refused, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return MachineSeatsView.Refused("invalid_request", null);
        if (_fleet!.Find(id) is not { } host) return MachineSeatsView.Refused("not_found", null);
        try
        {
            var seats = await host.ManageAsync((client, token) => client.ListSeatsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", null, seats.Limit, seats.Working, [.. seats.Queue.Select(static item => new MachineSeatItem(item.Id, item.SessionId, item.Title, item.Kind,
                item.Position, item.QueuedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)))]);
        }
        catch (ObjectDisposedException) { return MachineSeatsView.Refused(RemoteSessionHost.Describe(host.State), null); }
        catch (FleetException exception) { return MachineSeatsView.Refused(Code(exception), exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineSeatsView.Refused(RemoteSessionHost.MachineOfflineCode, exception.Message); }
    }

    /// <summary>Starts a run that waits for a seat of a machine now, past its limit.</summary>
    [NeoRpcMethod("startSeat")]
    public Task<MachineManageResult> StartSeatAsync(MachineSeatRequest request, CancellationToken cancellationToken)
        => SeatAsync(request, static (client, id, token) => client.StartSeatAsync(id, token), cancellationToken);

    /// <summary>Takes a run that waits for a seat of a machine out of its queue: it does not start.</summary>
    [NeoRpcMethod("cancelSeat")]
    public Task<MachineManageResult> CancelSeatAsync(MachineSeatRequest request, CancellationToken cancellationToken)
        => SeatAsync(request, static (client, id, token) => client.CancelSeatAsync(id, token), cancellationToken);

    private async Task<MachineManageResult> SeatAsync(MachineSeatRequest request,
        Func<CodeAlta.Fleet.Client.FleetNodeClient, string, CancellationToken, Task<CodeAlta.Fleet.NodeApi.FleetSeatResult>> call, CancellationToken cancellationToken)
    {
        if (request?.Id is not { Length: > 0 and <= 256 } id) return await Invalid(request?.ExpectedEpoch, "The request names the run that waits.").ConfigureAwait(false);
        var result = await ManageAsync(request.ExpectedEpoch, request.MachineId, async (client, token) => (await call(client, id, token).ConfigureAwait(false)).Status,
            cancellationToken).ConfigureAwait(false);
        // The machine answers whether the run still waited: one that started meanwhile is not found.
        return result is { Status: "ok", Message: "not_found" } ? new MachineManageResult("not_found", null) : result with { Message = result.Status == "ok" ? null : result.Message };
    }
}

/// <summary>The limits of a machine, as the person who manages it sees them.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Sessions">How many sessions work there at once; null for no limit.</param>
internal sealed record MachineLimitsView(string Status, string? Message, int? Sessions);

/// <summary>The limits to give a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Sessions">How many sessions work there at once; null for no limit.</param>
internal sealed record MachineLimitsRequest(string ExpectedEpoch, string MachineId, int? Sessions);

/// <summary>A run that waits for a seat of a machine.</summary>
/// <param name="Id">The run, which <c>startSeat</c> and <c>cancelSeat</c> name.</param>
/// <param name="SessionId">Its session; null for the run of an automation that has none yet.</param>
/// <param name="Title">What it is.</param>
/// <param name="Kind"><c>send</c>, <c>queue</c>, <c>agent</c> or <c>automation</c>.</param>
/// <param name="Position">Its place: 1 for the next one.</param>
/// <param name="QueuedAt">When it started to wait, in ISO 8601.</param>
internal sealed record MachineSeatItem(string Id, string? SessionId, string? Title, string Kind, int Position, string QueuedAt);

/// <summary>The seats of a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Limit">How many sessions work there at once; null for no limit.</param>
/// <param name="Working">The seats taken.</param>
/// <param name="Queue">The runs that wait, the next one first.</param>
internal sealed record MachineSeatsView(string Status, string? Message, int? Limit, int Working, MachineSeatItem[] Queue)
{
    internal static MachineSeatsView Refused(string status, string? message) => new(status, message, null, 0, []);
}

/// <summary>A run that waits for a seat of a machine, to start now or to cancel.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Id">The run.</param>
internal sealed record MachineSeatRequest(string ExpectedEpoch, string MachineId, string Id);
