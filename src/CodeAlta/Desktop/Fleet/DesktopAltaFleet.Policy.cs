using CodeAlta.Fleet.Protocol;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Fleet;

// The signed policy for `alta fleet policy` (doc/fleet/security-and-privacy.md, policies and the kill switch): what this computer
// verified, read again from the server when it answers. Nothing here signs.
internal sealed partial class DesktopAltaFleet : IAltaFleetPolicy
{
    /// <inheritdoc />
    public async Task<AltaFleetPolicy> ReadPolicyAsync(string? server, CancellationToken cancellationToken)
    {
        if (Servers is not { } servers || Connection(server) is not { } connection) return new("no_server", null);
        try
        {
            var policy = servers.PolicyOf(connection.Link.Id);
            string? refused = null;
            FleetPolicyState state;
            try
            {
                state = await policy.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (FleetPolicyDocumentException exception)
            {
                state = policy.Current ?? FleetPolicyState.Empty;
                refused = exception.Message;
            }

            IReadOnlyDictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (state.Stop is { Scope: not FleetPolicyStopScopes.Fleet })
            {
                var read = await servers.ReadAccessAsync(connection.Link.Id, cancellationToken).ConfigureAwait(false);
                names = read.Devices.Values.Select(static device => (device.Id, device.Name)).Concat(read.Teams.Select(static team => (team.Id, team.Name)))
                    .DistinctBy(static pair => pair.Item1).ToDictionary(static pair => pair.Item1, static pair => pair.Item2, StringComparer.Ordinal);
            }

            return new AltaFleetPolicy("ok", null)
            {
                Server = connection.Link.Url,
                Number = state.Number,
                SignedAt = state.Time,
                Rules = FleetPolicyDocuments.Describe(FleetPolicyState.Empty, new FleetPolicyDocument(FleetPolicyDocument.CurrentVersion, "-", 1, null, 1, 0, state.Rules, null, "-",
                    default), static _ => null),
                Stop = state.Stop is { } stop
                    ? new AltaFleetPolicyStop(stop.Scope, [.. stop.Machines.Concat(stop.Teams).Select(id => names.GetValueOrDefault(id, id))], stop.Reason)
                    : null,
                StopsThisComputer = servers.Policy.Halt is not null,
                Refused = refused,
            };
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message);
        }
    }
}
