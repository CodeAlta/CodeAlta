using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Fleet;

// The audit log for `alta fleet audit` (doc/fleet/security-and-privacy.md, audit log): read with the access of the person of
// this computer; the server refuses whoever may not read it.
internal sealed partial class DesktopAltaFleet : IAltaFleetAudit
{
    /// <inheritdoc />
    public async Task<AltaFleetAuditPage> ReadAuditAsync(string? server, AltaFleetAuditFilters filters, long? before, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (Servers is not { } servers || Connection(server) is not { } connection) return new("no_server", null);
        var query = new FleetAuditQuery
        {
            Actions = filters.Actions, PersonId = filters.Person, DeviceId = filters.Device, Result = filters.Result, Target = filters.Target, From = filters.From, To = filters.To,
            Text = filters.Text, Newest = true, Before = before, Limit = Math.Clamp(limit, 1, FleetAuditQuery.MaxPage),
        };
        // The filters are checked here as the server checks them, so that a mistake says what is wrong.
        if (!FleetAuditQuery.TryParse(Values(query), out _, out var problem)) return new("invalid_request", problem);
        try
        {
            var page = await servers.AccessCallAsync(connection.Link.Id, (client, token) => client.ListAuditAsync(query, token), cancellationToken).ConfigureAwait(false);
            return new AltaFleetAuditPage("ok", null)
            {
                Server = connection.Link.Url,
                Records = [.. page.Items.Select(static record => new AltaFleetAuditRecord(record.Sequence, record.RecordedAt, record.Action, record.Result,
                    record.ActorName ?? record.ActorPersonId, record.DeviceName ?? record.ActorDeviceId, record.Target, record.SourceAddress, record.Details))],
                Before = page.Next is { } next && long.TryParse(next, System.Globalization.CultureInfo.InvariantCulture, out var cursor) ? cursor : null,
            };
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message);
        }
    }

    /// <inheritdoc />
    public async Task<AltaFleetAuditCheck> VerifyAuditAsync(string? server, CancellationToken cancellationToken)
    {
        if (Servers is not { } servers || Connection(server) is not { } connection) return new("no_server", null, false, 0, null, null);
        try
        {
            var check = await servers.AccessCallAsync(connection.Link.Id, static (client, token) => client.VerifyAuditAsync(token), cancellationToken).ConfigureAwait(false);
            return new("ok", null, check.Valid, check.Count, check.BrokenAt, check.HeadHash);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, false, 0, null, null);
        }
    }

    // The parameters of the query, as the request carries them.
    private static Func<string, string?> Values(FleetAuditQuery query)
    {
        var pairs = query.ToQueryString().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(static pair => pair.Split('=', 2))
            .ToDictionary(static pair => pair[0], static pair => (string?)Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty), StringComparer.Ordinal);
        return name => pairs.GetValueOrDefault(name);
    }
}
