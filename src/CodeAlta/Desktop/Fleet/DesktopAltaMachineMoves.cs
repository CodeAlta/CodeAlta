using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// <c>alta session move</c> in CodeAlta Desktop: the machines are this computer and its paired machines
/// (<see cref="DesktopFleet"/>), the moves those of <see cref="DesktopMoves"/>.
/// </summary>
/// <param name="moves">The moves of this computer.</param>
/// <param name="fleet">Its paired machines.</param>
/// <param name="host">Its runtime.</param>
internal sealed class DesktopAltaMachineMoves(DesktopMoves moves, DesktopFleet fleet, CodeAltaHost host) : IAltaSessionMachineMoves
{
    /// <inheritdoc />
    public IReadOnlyList<AltaMoveMachine> ListMachines()
        => [new AltaMoveMachine("", moves.MachineName, true, "online"),
            .. fleet.Hosts.Select(machine => new AltaMoveMachine(machine.MachineId!, machine.Listing.Info?.Name ?? machine.Machine.Name, false,
                RemoteSessionHost.Describe(machine.State)))];

    /// <inheritdoc />
    public async Task<AltaSessionMoveResult> MoveAsync(string sessionId, string machine, bool cloneRepository, CancellationToken cancellationToken)
    {
        if (Resolve(machine) is not { } target) return Refused("machine_not_found", $"No machine is named '{machine}': `alta session move --list` lists them.", machine);
        var (found, source) = await SourceOfAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!found) return Refused("session_missing", "The session runs neither here nor on a paired machine that is online.", target.Name);
        try
        {
            var result = await moves.MoveAsync(sessionId, source, target.Id, cloneRepository, cancellationToken).ConfigureAwait(false);
            return new AltaSessionMoveResult(result.Status, result.Code, result.Message, target.Name, result.WorkFolder, result.Warnings);
        }
        catch (ObjectDisposedException exception) { return Refused("machine_offline", exception.Message, target.Name); }
        catch (KeyNotFoundException exception) { return Refused("machine_not_found", exception.Message, target.Name); }
        catch (FleetException exception) { return Refused(exception.Error.Code, exception.Error.Message, target.Name); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return Refused("machine_offline", exception.Message, target.Name); }
    }

    /// <inheritdoc />
    public AltaSessionMoveResult Schedule(string sessionId, string machine, bool cloneRepository)
    {
        if (Resolve(machine) is not { } target) return Refused("machine_not_found", $"No machine is named '{machine}': `alta session move --list` lists them.", machine);
        if (target.Id is null) return Refused("same_machine", "The session runs here already.", target.Name);
        moves.ScheduleMove(sessionId, target.Id, target.Name, cloneRepository);
        return new AltaSessionMoveResult("scheduled", null, null, target.Name, null, []);
    }

    // The machine a person or an agent names: this computer (null id) or a paired machine, by name or id.
    private (string? Id, string Name)? Resolve(string machine)
    {
        var name = machine.Trim();
        if (string.Equals(name, moves.MachineName, StringComparison.OrdinalIgnoreCase)) return (null, moves.MachineName);
        var found = fleet.Hosts.Where(host => string.Equals(host.MachineId, name, StringComparison.Ordinal)
            || string.Equals(host.Listing.Info?.Name ?? host.Machine.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return found.Length == 1 ? (found[0].MachineId, found[0].Listing.Info?.Name ?? found[0].Machine.Name) : null;
    }

    // Where a session runs: this computer (null) when it has it and did not give it away, else the machine that lists it.
    private async Task<(bool Found, string? MachineId)> SourceOfAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await host.RuntimeService.TryGetRecoverableSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is not null
            && host.RuntimeService.Relinquishments.Read(sessionId) is not { IsRelinquished: true })
            return (true, null);
        return fleet.Hosts.FirstOrDefault(machine => machine.Lists(sessionId)) is { } remote ? (true, remote.MachineId) : (false, null);
    }

    private static AltaSessionMoveResult Refused(string code, string? message, string? machine) => new("refused", code, message, machine, null, []);
}
